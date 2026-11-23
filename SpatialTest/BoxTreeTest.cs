using System.Runtime.InteropServices;
using FluentAssertions;

namespace Yewnyx.Spatial.Test;

public class BoxTreeTest {
    // Unit cubes in a row along x, a gap of 1 between them:
    //   0: x 0..1    1: x 2..3    2: x 4..5    3: x 6..7    4: x 8..9      (y and z 0..1)
    private static Box Cube(float x) => new(x, 0, 0, x + 1, 1, 1);
    private static readonly Box[] Row = [Cube(0), Cube(2), Cube(4), Cube(6), Cube(8)];
    private static readonly Box Extent = new(0, 0, 0, 1000, 1, 1);

    private sealed class Tree {
        public readonly Box[] Boxes;
        public readonly BoxNode[] Nodes;
        public readonly int[] Order;
        public readonly int NodeCount;

        public Tree(Box[] boxes) {
            Boxes = boxes;
            Nodes = new BoxNode[BoxTree.NodesLength(boxes.Length)];
            Order = new int[boxes.Length];
            var workspace = new int[BoxTree.WorkspaceLength(boxes.Length)];
            BoxTree.Build(boxes, Extent, Nodes, Order, workspace, out NodeCount).Should().BeTrue();
        }

        public int[] InSphere(float x, float y, float z, float radius, out int total) {
            var results = new int[Boxes.Length];
            var written = BoxTree.InSphere(Nodes.AsSpan(0, NodeCount), Order, Boxes, x, y, z, radius, results, out total);
            var sorted = results[..Math.Max(written, 0)];
            Array.Sort(sorted);
            return sorted;
        }

        public (int[] results, float[] distances) Nearest(float x, float y, float z, float maxDistanceSquared, int k) {
            var results = new int[k];
            var distances = new float[k];
            var written = BoxTree.Nearest(Nodes.AsSpan(0, NodeCount), Order, Boxes, x, y, z, maxDistanceSquared, results, distances);
            return (results[..written], distances[..written]);
        }
    }

    // The same query on a scan of the same boxes.
    private static (int[] results, float[] distances) ScanNearest(Box[] boxes, float x, float y, float z, float maxDistanceSquared, int k) {
        var n = boxes.Length;
        float[] minX = new float[n], minY = new float[n], minZ = new float[n], maxX = new float[n], maxY = new float[n], maxZ = new float[n];
        BoxScan.Pack(boxes, minX, minY, minZ, maxX, maxY, maxZ).Should().BeTrue();
        var results = new int[k];
        var distances = new float[k];
        var written = BoxScan.Nearest(new BoxColumns(minX, minY, minZ, maxX, maxY, maxZ), x, y, z, maxDistanceSquared, results, distances);
        return (results[..written], distances[..written]);
    }

    [Fact]
    public void TheTreeSplitsTheSortedBoxesAtTheMiddle() {
        var tree = new Tree(Row);

        // Five boxes sorted along x: the root, then a leaf of two, then a leaf of three.
        tree.NodeCount.Should().Be(3);
        tree.Order.Should().Equal(0, 1, 2, 3, 4);
        (tree.Nodes[0].Count, tree.Nodes[0].First).Should().Be((0, 2));
        (tree.Nodes[1].First, tree.Nodes[1].Count).Should().Be((0, 2));
        (tree.Nodes[2].First, tree.Nodes[2].Count).Should().Be((2, 3));

        // Each node's box is the one around what lies beneath it.
        (tree.Nodes[0].MinX, tree.Nodes[0].MaxX).Should().Be((0f, 9f));
        (tree.Nodes[1].MinX, tree.Nodes[1].MaxX).Should().Be((0f, 3f));
        (tree.Nodes[2].MinX, tree.Nodes[2].MaxX).Should().Be((4f, 9f));
        (tree.Nodes[0].MinY, tree.Nodes[0].MaxY, tree.Nodes[0].MinZ, tree.Nodes[0].MaxZ).Should().Be((0f, 1f, 0f, 1f));
    }

    [Fact]
    public void InSphereFindsTheBoxesWithinTheRadius() {
        var tree = new Tree(Row);

        // From the middle of cube 2, cubes 1 and 3 are 1.5 away and cubes 0 and 4 are 3.5 away.
        tree.InSphere(4.5f, 0.5f, 0.5f, 1.6f, out var total).Should().Equal(1, 2, 3);
        total.Should().Be(3);
        tree.InSphere(4.5f, 0.5f, 0.5f, 1.5f, out _).Should().Equal(1, 2, 3);
        tree.InSphere(4.5f, 0.5f, 0.5f, 1.4f, out _).Should().Equal(2);
        tree.InSphere(4.5f, 3f, 0.5f, 1.9f, out _).Should().BeEmpty();
    }

    [Fact]
    public void TheSameBoxesMatchInWhateverOrderTheyAreGiven() {
        // Row backwards: box i here is cube 4 - i.
        var tree = new Tree([Cube(8), Cube(6), Cube(4), Cube(2), Cube(0)]);

        tree.Order.Should().Equal(4, 3, 2, 1, 0);
        tree.InSphere(4.5f, 0.5f, 0.5f, 1.6f, out _).Should().Equal(1, 2, 3);
        tree.InSphere(0.5f, 0.5f, 0.5f, 1.6f, out _).Should().Equal(3, 4);
    }

    [Fact]
    public void ResultsStopAtTheBufferButTheTotalCountsEverything() {
        var tree = new Tree(Row);
        int[] buffer = [-1, -1, -1, -1];

        BoxTree.InSphere(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, buffer.AsSpan(1, 2), out var total).Should().Be(2);
        total.Should().Be(5);
        (buffer[0], buffer[3]).Should().Be((-1, -1));
        buffer[1..3].Should().OnlyHaveUniqueItems().And.OnlyContain(item => item >= 0 && item < 5);
    }

    [Fact]
    public void NearestMatchesTheScanIncludingTies() {
        var tree = new Tree(Row);

        // Cubes 1 and 3 are equally far from the middle of cube 2, so the lower index comes first.
        tree.Nearest(4.5f, 0.5f, 0.5f, 100, 3).results.Should().Equal(2, 1, 3);
        tree.Nearest(4.5f, 0.5f, 0.5f, 100, 3).distances.Should().Equal(0, 2.25f, 2.25f);
        tree.Nearest(4.5f, 0.5f, 0.5f, 100, 2).results.Should().Equal(2, 1);
        tree.Nearest(4.5f, 0.5f, 0.5f, 2.25f, 5).results.Should().Equal(2, 1, 3);
        tree.Nearest(4.5f, 0.5f, 0.5f, 2f, 5).results.Should().Equal(2);

        // Given backwards, the tie goes the other way: the lower index is now the cube on the right.
        var backwards = new Tree([Cube(8), Cube(6), Cube(4), Cube(2), Cube(0)]);
        backwards.Nearest(4.5f, 0.5f, 0.5f, 100, 2).results.Should().Equal(2, 1);

        foreach (var k in new[] { 1, 2, 3, 4, 5, 6 }) {
            var (results, distances) = tree.Nearest(4.5f, 0.5f, 0.5f, 100, k);
            var scan = ScanNearest(Row, 4.5f, 0.5f, 0.5f, 100, k);
            results.Should().Equal(scan.results);
            distances.Should().Equal(scan.distances);
        }
    }

    [Fact]
    public void ManyBoxesWithManyTies() {
        // A 12 x 12 x 3 block of unit cubes, two apart: many are equally far from any point.
        var boxes = new Box[432];
        for (var i = 0; i < boxes.Length; i++) {
            float x = 2 * (i % 12), y = 2 * (i / 12 % 12), z = 2 * (i / 144);
            boxes[i] = new Box(x, y, z, x + 1, y + 1, z + 1);
        }
        var extent = new Box(0, 0, 0, 24, 24, 6);
        var nodes = new BoxNode[BoxTree.NodesLength(boxes.Length)];
        var order = new int[boxes.Length];
        BoxTree.Build(boxes, extent, nodes, order, new int[BoxTree.WorkspaceLength(boxes.Length)], out var nodeCount).Should().BeTrue();

        // Around the cube in the middle of the bottom layer: itself and its four neighbours in that
        // layer are within 1, and the one above it is 1 away too.
        var results = new int[boxes.Length];
        var written = BoxTree.InSphere(nodes.AsSpan(0, nodeCount), order, boxes, 10.5f, 10.5f, 0.5f, 1.5f, results, out var total);
        total.Should().Be(6);
        var found = results[..written];
        Array.Sort(found);
        found.Should().Equal(53, 64, 65, 66, 77, 209);

        var nearest = new int[9];
        var distances = new float[9];
        foreach (var point in new (float x, float y, float z)[] { (10.5f, 10.5f, 0.5f), (11.5f, 11.5f, 1.5f), (0, 0, 0), (30, 12, 3) }) {
            var count = BoxTree.Nearest(nodes.AsSpan(0, nodeCount), order, boxes, point.x, point.y, point.z, 400, nearest, distances);
            var scan = ScanNearest(boxes, point.x, point.y, point.z, 400, 9);
            nearest[..count].Should().Equal(scan.results);
            distances[..count].Should().Equal(scan.distances);
        }
    }

    [Fact]
    public void ABoxOutsideTheExtentIsStillFound() {
        // The extent only reaches x = 1000; the last cube is far beyond it.
        var tree = new Tree([Cube(0), Cube(2), Cube(5000)]);

        tree.InSphere(5000.5f, 0.5f, 0.5f, 1, out _).Should().Equal(2);
        tree.Nearest(4000, 0.5f, 0.5f, float.PositiveInfinity, 1).results.Should().Equal(2);
    }

    [Fact]
    public void NoBoxesAndOneBox() {
        var none = new Tree([]);
        none.NodeCount.Should().Be(0);
        none.InSphere(0, 0, 0, 100, out var total).Should().BeEmpty();
        total.Should().Be(0);
        none.Nearest(0, 0, 0, 100, 3).results.Should().BeEmpty();

        var one = new Tree([Cube(4)]);
        one.NodeCount.Should().Be(1);
        one.InSphere(4.5f, 0.5f, 0.5f, 0, out _).Should().Equal(0);
        one.Nearest(0, 0, 0, 100, 3).results.Should().Equal(0);
    }

    [Fact]
    public void BoxesThatAreAllTheSame() {
        var boxes = new Box[10];
        Array.Fill(boxes, Cube(4));
        var tree = new Tree(boxes);

        tree.InSphere(4.5f, 0.5f, 0.5f, 0, out var total).Should().Equal(0, 1, 2, 3, 4, 5, 6, 7, 8, 9);
        total.Should().Be(10);
        tree.Nearest(0, 0.5f, 0.5f, 100, 3).results.Should().Equal(0, 1, 2);
    }

    [Fact]
    public void BuildRefusesShortBuffers() {
        var nodes = new BoxNode[BoxTree.NodesLength(5)];
        int[] order = [-1, -1, -1, -1, -1];
        var workspace = new int[BoxTree.WorkspaceLength(5)];

        // Each buffer in turn is one too short. Nothing is written.
        BoxTree.Build(Row, Extent, nodes.AsSpan(0, 4), order, workspace, out var nodeCount).Should().BeFalse();
        BoxTree.Build(Row, Extent, nodes, order.AsSpan(0, 4), workspace, out nodeCount).Should().BeFalse();
        BoxTree.Build(Row, Extent, nodes, order, workspace.AsSpan(0, workspace.Length - 1), out nodeCount).Should().BeFalse();
        nodeCount.Should().Be(0);
        order.Should().Equal(-1, -1, -1, -1, -1);
    }

    [Fact]
    public void QueriesRefuseSpansThatDoNotBelongTogether() {
        var tree = new Tree(Row);
        var nodes = tree.Nodes.AsSpan(0, tree.NodeCount);
        int[] results = [-1, -1];
        float[] distances = [-1, -1];

        // Order for five boxes, but only four boxes.
        BoxTree.InSphere(nodes, tree.Order, Row.AsSpan(0, 4), 4.5f, 0.5f, 0.5f, 100, results, out var total).Should().Be(-1);
        total.Should().Be(-1);
        BoxTree.Nearest(nodes, tree.Order, Row.AsSpan(0, 4), 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(-1);

        // Boxes, but no tree.
        BoxTree.InSphere([], tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, out total).Should().Be(-1);
        total.Should().Be(-1);
        BoxTree.Nearest([], tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(-1);

        results.Should().Equal(-1, -1);
        distances.Should().Equal(-1, -1);
    }

    [Fact]
    public void DamagedNodesAreRefusedWithoutReadingOutOfBounds() {
        var results = new int[5];
        var distances = new float[5];

        // A right child that isn't above its parent would loop for ever.
        var tree = new Tree(Row);
        tree.Nodes[0].First = 0;
        BoxTree.InSphere(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, out var total).Should().Be(-1);
        total.Should().Be(-1);
        BoxTree.Nearest(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(-1);

        // A right child beyond the nodes.
        tree = new Tree(Row);
        tree.Nodes[0].First = 3;
        BoxTree.InSphere(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, out total).Should().Be(-1);
        total.Should().Be(-1);

        // A leaf that reaches past the boxes.
        tree = new Tree(Row);
        tree.Nodes[2].Count = 4;
        BoxTree.InSphere(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, out total).Should().Be(-1);
        total.Should().Be(-1);
        BoxTree.Nearest(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(-1);

        // An order entry that names no box.
        tree = new Tree(Row);
        tree.Order[3] = 5;
        BoxTree.InSphere(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, out total).Should().Be(-1);
        total.Should().Be(-1);
        BoxTree.Nearest(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(-1);
    }

    [Fact]
    public void ARadiusOrPointThatMakesNoSenseMatchesNothing() {
        var tree = new Tree(Row);

        tree.InSphere(4.5f, 0.5f, 0.5f, -1, out var total).Should().BeEmpty();
        total.Should().Be(0);
        tree.InSphere(4.5f, 0.5f, 0.5f, float.NaN, out _).Should().BeEmpty();
        tree.InSphere(float.NaN, 0.5f, 0.5f, 100, out _).Should().BeEmpty();
        tree.Nearest(4.5f, float.NaN, 0.5f, 100, 3).results.Should().BeEmpty();
        tree.Nearest(4.5f, 0.5f, 0.5f, float.NaN, 3).results.Should().BeEmpty();
    }

    [Fact]
    public void ANotANumberCoordinateLeavesThatSideOpenAsInTheScan() {
        // Cube 1 (x 2..3) with no lower x reaches down the row without limit, and still ends at 3.
        var openBelow = new Tree([Cube(0), new(float.NaN, 0, 0, 3, 1, 1), Cube(4), Cube(6), Cube(8), Cube(10), Cube(12), Cube(14), Cube(16)]);
        openBelow.InSphere(-50, 0.5f, 0.5f, 0.1f, out _).Should().Equal(1);
        openBelow.InSphere(4.5f, 0.5f, 0.5f, 1.4f, out _).Should().Equal(2);
        openBelow.InSphere(4.5f, 0.5f, 0.5f, 1.6f, out _).Should().Equal(1, 2, 3);
        openBelow.InSphere(16.5f, 0.5f, 0.5f, 0.1f, out _).Should().Equal(8);

        // Whichever coordinates are missing, the tree answers exactly as the scan does.
        foreach (var open in new Box[] {
                     new(float.NaN, 0, 0, 3, 1, 1), new(2, 0, 0, float.NaN, 1, 1), new(2, float.NaN, 0, 3, 1, 1),
                     new(2, 0, 0, 3, 1, float.NaN), new(float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN),
                 }) {
            Box[] boxes = [Cube(0), open, Cube(4), Cube(6), Cube(8), Cube(10), Cube(12), Cube(14), Cube(16)];
            var tree = new Tree(boxes);
            foreach (var point in new (float x, float y, float z)[] { (4.5f, 0.5f, 0.5f), (0.5f, 0.5f, 0.5f), (16.5f, 0.5f, 0.5f), (-50, 0.5f, 0.5f), (2.5f, -40, 0.5f), (2.5f, 0.5f, 90) }) {
                foreach (var radius in new[] { 0.1f, 1.6f, 4f }) {
                    var scan = ScanNearest(boxes, point.x, point.y, point.z, radius * radius, 9);
                    var nearest = tree.Nearest(point.x, point.y, point.z, radius * radius, 9);
                    nearest.results.Should().Equal(scan.results);
                    nearest.distances.Should().Equal(scan.distances);
                    // Nearest with room for every box holds exactly what InSphere finds.
                    tree.InSphere(point.x, point.y, point.z, radius, out var total).Should().Equal(scan.results.Order());
                    total.Should().Be(scan.results.Length);
                }
            }
        }
    }

    [Fact]
    public void AnExtentThatMakesNoSenseOnlyCostsSpeed() {
        var nodes = new BoxNode[BoxTree.NodesLength(5)];
        var order = new int[5];
        var results = new int[5];

        foreach (var extent in new Box[] { new(float.NaN, 0, 0, 1, 1, 1), new(5, 5, 5, 5, 5, 5), new(9, 1, 1, 0, 0, 0) }) {
            BoxTree.Build(Row, extent, nodes, order, new int[BoxTree.WorkspaceLength(5)], out var nodeCount).Should().BeTrue();
            var written = BoxTree.InSphere(nodes.AsSpan(0, nodeCount), order, Row, 4.5f, 0.5f, 0.5f, 1.6f, results, out _);
            var found = results[..written];
            Array.Sort(found);
            found.Should().Equal(1, 2, 3);
        }
    }

    [Fact]
    public void ALongRowDoesNotOverflowTheStacks() {
        // 200,000 cubes in one row: the tree is as deep as it gets for this many.
        var boxes = new Box[200_000];
        for (var i = 0; i < boxes.Length; i++) { boxes[i] = Cube(2 * i); }
        var extent = new Box(0, 0, 0, 400_000, 1, 1);
        var nodes = new BoxNode[BoxTree.NodesLength(boxes.Length)];
        var order = new int[boxes.Length];
        BoxTree.Build(boxes, extent, nodes, order, new int[BoxTree.WorkspaceLength(boxes.Length)], out var nodeCount).Should().BeTrue();
        var results = new int[4];
        var distances = new float[4];

        BoxTree.InSphere(nodes.AsSpan(0, nodeCount), order, boxes, 300_000.5f, 0.5f, 0.5f, 1.6f, results, out var total).Should().Be(3);
        total.Should().Be(3);
        Array.Sort(results, 0, 3);
        results[..3].Should().Equal(149_999, 150_000, 150_001);
        BoxTree.Nearest(nodes.AsSpan(0, nodeCount), order, boxes, 300_000.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(4);
        results.Should().Equal(150_000, 149_999, 150_001, 149_998);
    }

    [Fact]
    public void AWarmBuildAndQueryDontAllocate() {
        var nodes = new BoxNode[BoxTree.NodesLength(5)];
        var order = new int[5];
        var workspace = new int[BoxTree.WorkspaceLength(5)];
        var results = new int[5];
        var distances = new float[5];
        BoxTree.Build(Row, Extent, nodes, order, workspace, out var nodeCount);
        BoxTree.InSphere(nodes.AsSpan(0, nodeCount), order, Row, 4.5f, 0.5f, 0.5f, 100, results, out _);
        BoxTree.Nearest(nodes.AsSpan(0, nodeCount), order, Row, 4.5f, 0.5f, 0.5f, 100, results, distances);

        var before = GC.GetAllocatedBytesForCurrentThread();
        BoxTree.Build(Row, Extent, nodes, order, workspace, out nodeCount);
        BoxTree.InSphere(nodes.AsSpan(0, nodeCount), order, Row, 4.5f, 0.5f, 0.5f, 100, results, out _);
        BoxTree.Nearest(nodes.AsSpan(0, nodeCount), order, Row, 4.5f, 0.5f, 0.5f, 100, results, distances);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }

    [Fact]
    public void NodesDeeperThanAnyBuiltTreeAreRefused() {
        // A hand-made chain 70 deep: node i has a leaf on its right and node i + 1 on its left.
        // Every check on a single node passes; only the room on the walk's stack runs out.
        const int depth = 70;
        var nodes = new BoxNode[2 * depth + 1];
        for (var i = 0; i < nodes.Length; i++) {
            nodes[i] = new BoxNode { MinX = 0, MinY = 0, MinZ = 0, MaxX = 1, MaxY = 1, MaxZ = 1, First = 0, Count = 1 };
        }
        for (var i = 0; i < depth; i++) {
            nodes[i].Count = 0;
            nodes[i].First = i < depth - 1 ? depth + 1 + i : 2 * depth;
        }
        Box[] boxes = [Cube(0)];
        int[] order = [0];
        var results = new int[4];
        var distances = new float[4];

        BoxTree.InSphere(nodes, order, boxes, 0.5f, 0.5f, 0.5f, 1, results, out var total).Should().Be(-1);
        total.Should().Be(-1);
        BoxTree.Nearest(nodes, order, boxes, 0.5f, 0.5f, 0.5f, 1, results, distances).Should().Be(-1);
    }

    [Fact]
    public void NodesThatShareChildrenAreRefused() {
        // Each node's children are the next two nodes, so the two leaves at the end are reached
        // along a great many paths. A tree is walked once per node, so the walk gives up.
        var nodes = new BoxNode[30];
        for (var i = 0; i < nodes.Length; i++) {
            nodes[i] = new BoxNode { MinX = 0, MinY = 0, MinZ = 0, MaxX = 1, MaxY = 1, MaxZ = 1, First = i + 2, Count = 0 };
        }
        nodes[28] = nodes[29] = new BoxNode { MinX = 0, MinY = 0, MinZ = 0, MaxX = 1, MaxY = 1, MaxZ = 1, First = 0, Count = 1 };
        Box[] boxes = [Cube(0)];
        int[] order = [0];
        var results = new int[4];
        var distances = new float[4];

        BoxTree.InSphere(nodes, order, boxes, 0.5f, 0.5f, 0.5f, 1, results, out var total).Should().Be(-1);
        total.Should().Be(-1);
        BoxTree.Nearest(nodes, order, boxes, 0.5f, 0.5f, 0.5f, float.PositiveInfinity, results, distances).Should().Be(-1);
    }

    [Fact]
    public void ALeafThatStartsBeforeTheBoxesIsRefused() {
        var tree = new Tree(Row);
        tree.Nodes[1].First = -1;
        var results = new int[5];
        var distances = new float[5];

        BoxTree.InSphere(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 0.5f, 0.5f, 0.5f, 100, results, out var total).Should().Be(-1);
        total.Should().Be(-1);
        BoxTree.Nearest(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 0.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(-1);
    }

    [Fact]
    public void NearestWritesAsManyAsTheShorterBufferHolds() {
        var tree = new Tree(Row);
        int[] results = [-1, -1, -1];
        float[] distances = [-1];

        BoxTree.Nearest(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(1);
        results.Should().Equal(2, -1, -1);
        distances.Should().Equal(0);

        results = [-1];
        distances = [-1, -1, -1];
        BoxTree.Nearest(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, results, distances).Should().Be(1);
        results.Should().Equal(2);
        distances.Should().Equal(0, -1, -1);

        // No room at all.
        BoxTree.Nearest(tree.Nodes.AsSpan(0, tree.NodeCount), tree.Order, Row, 4.5f, 0.5f, 0.5f, 100, [], []).Should().Be(0);
    }

    [Fact]
    public void ANodeIsTheBoxAroundBothItsChildren() {
        // Six boxes along the diagonal, so the root's left leaf holds boxes 0 to 2 and its right
        // leaf boxes 3 to 5. One box is far bigger than the rest, with a different size on each axis.
        static Box Small(float at) => new(at - 0.5f, at - 0.5f, at - 0.5f, at + 0.5f, at + 0.5f, at + 0.5f);
        var extent = new Box(0, 0, 0, 16, 16, 16);
        var nodes = new BoxNode[6];
        var order = new int[6];
        var workspace = new int[BoxTree.WorkspaceLength(6)];

        // The big box is in the left leaf: every side of the root comes from the left child.
        Box[] bigOnTheLeft = [Small(1), Small(2), new(-17, -18, -19, 23, 24, 25), Small(10), Small(11), Small(12)];
        BoxTree.Build(bigOnTheLeft, extent, nodes, order, workspace, out var nodeCount).Should().BeTrue();
        nodeCount.Should().Be(3);
        order.Should().Equal(0, 1, 2, 3, 4, 5);
        (nodes[0].MinX, nodes[0].MinY, nodes[0].MinZ, nodes[0].MaxX, nodes[0].MaxY, nodes[0].MaxZ).Should().Be((-17f, -18f, -19f, 23f, 24f, 25f));
        (nodes[1].MinX, nodes[1].MinY, nodes[1].MinZ, nodes[1].MaxX, nodes[1].MaxY, nodes[1].MaxZ).Should().Be((-17f, -18f, -19f, 23f, 24f, 25f));
        (nodes[2].MinX, nodes[2].MinY, nodes[2].MinZ, nodes[2].MaxX, nodes[2].MaxY, nodes[2].MaxZ).Should().Be((9.5f, 9.5f, 9.5f, 12.5f, 12.5f, 12.5f));

        // The big box is in the right leaf: every side of the root comes from the right child.
        Box[] bigOnTheRight = [Small(1), Small(2), Small(3), Small(10), new(-9, -10, -11, 31, 32, 33), Small(12)];
        BoxTree.Build(bigOnTheRight, extent, nodes, order, workspace, out nodeCount).Should().BeTrue();
        nodeCount.Should().Be(3);
        order.Should().Equal(0, 1, 2, 3, 4, 5);
        (nodes[0].MinX, nodes[0].MinY, nodes[0].MinZ, nodes[0].MaxX, nodes[0].MaxY, nodes[0].MaxZ).Should().Be((-9f, -10f, -11f, 31f, 32f, 33f));
        (nodes[1].MinX, nodes[1].MinY, nodes[1].MinZ, nodes[1].MaxX, nodes[1].MaxY, nodes[1].MaxZ).Should().Be((0.5f, 0.5f, 0.5f, 3.5f, 3.5f, 3.5f));
        (nodes[2].MinX, nodes[2].MinY, nodes[2].MinZ, nodes[2].MaxX, nodes[2].MaxY, nodes[2].MaxZ).Should().Be((-9f, -10f, -11f, 31f, 32f, 33f));
    }

    [Fact]
    public void BoxesAreSortedAlongAllThreeAxes() {
        // Eight small boxes at the corners of a cube, given from the far corner back to the near one:
        // box i sits at x high if bit 0 of 7 - i is set, y high for bit 1, z high for bit 2.
        var boxes = new Box[8];
        for (var i = 0; i < 8; i++) {
            var corner = 7 - i;
            float x = (corner & 1) != 0 ? 600 : 100, y = (corner & 2) != 0 ? 600 : 100, z = (corner & 4) != 0 ? 600 : 100;
            boxes[i] = new Box(x, y, z, x + 1, y + 1, z + 1);
        }
        var nodes = new BoxNode[8];
        var order = new int[8];

        // The order goes round the corners one step along one axis at a time, whichever way the boxes
        // were given: from the near corner up z, across y, down z, across x, up z, back y, down z.
        BoxTree.Build(boxes, new Box(0, 0, 0, 1024, 1024, 1024), nodes, order, new int[BoxTree.WorkspaceLength(8)], out _).Should().BeTrue();
        order.Select(i => 7 - i).Should().Equal(0b000, 0b100, 0b110, 0b010, 0b011, 0b111, 0b101, 0b001);
    }

    // A cell's place along the Hilbert curve through 1024 cells a side, worked out the plain way
    // (Skilling, "Programming the Hilbert curve", 2004), x's bit the highest of each three.
    private static int HilbertKey(int cellX, int cellY, int cellZ) {
        uint x = (uint)cellX, y = (uint)cellY, z = (uint)cellZ;
        for (var bit = 1u << 9; bit > 1; bit >>= 1) {
            var low = bit - 1;
            if ((x & bit) != 0) { x ^= low; }
            if ((y & bit) != 0) { x ^= low; } else { var swap = (x ^ y) & low; x ^= swap; y ^= swap; }
            if ((z & bit) != 0) { x ^= low; } else { var swap = (x ^ z) & low; x ^= swap; z ^= swap; }
        }
        y ^= x;
        z ^= y;
        var turn = 0u;
        for (var bit = 1u << 9; bit > 1; bit >>= 1) { if ((z & bit) != 0) { turn ^= bit - 1; } }
        x ^= turn; y ^= turn; z ^= turn;
        var key = 0;
        for (var bit = 9; bit >= 0; bit--) { key = (key << 3) | (int)((((x >> bit) & 1) << 2) | (((y >> bit) & 1) << 1) | ((z >> bit) & 1)); }
        return key;
    }

    [Fact]
    public void TheOrderIsThatOfTheHilbertCurve() {
        // 24 blocks of 16 x 16 x 16 cells, with a small box in the middle of every cell. The curve can
        // lie 24 ways round in a block, and it enters these one in each way. So between them they
        // read every entry of the table the tree makes its keys from, and a fault in any one entry
        // changes the order.
        (int x, int y, int z)[] blocks = [
            (0, 0, 0), (576, 752, 848), (416, 464, 256), (320, 560, 160), (256, 192, 688), (400, 688, 976), (32, 560, 592), (864, 480, 96),
            (96, 928, 80), (352, 96, 768), (128, 480, 688), (560, 976, 544), (960, 656, 512), (528, 144, 640), (640, 96, 336), (144, 1008, 672),
            (720, 224, 128), (240, 416, 384), (176, 48, 896), (480, 832, 768), (272, 720, 656), (976, 416, 816), (672, 656, 944), (160, 272, 592),
        ];
        var cells = new (int x, int y, int z)[blocks.Length * 4096];
        var boxes = new Box[cells.Length];
        for (var i = 0; i < cells.Length; i++) {
            var block = blocks[i / 4096];
            var cell = cells[i] = (block.x + i % 16, block.y + i / 16 % 16, block.z + i / 256 % 16);
            boxes[i] = new Box(cell.x + 0.25f, cell.y + 0.25f, cell.z + 0.25f, cell.x + 0.75f, cell.y + 0.75f, cell.z + 0.75f);
        }
        var nodes = new BoxNode[boxes.Length];
        var order = new int[boxes.Length];
        BoxTree.Build(boxes, new Box(0, 0, 0, 1024, 1024, 1024), nodes, order, new int[BoxTree.WorkspaceLength(boxes.Length)], out _).Should().BeTrue();

        // The plain computation must put the boxes in the order the tree did.
        var expected = Enumerable.Range(0, boxes.Length).OrderBy(i => HilbertKey(cells[i].x, cells[i].y, cells[i].z)).ToArray();
        order.AsSpan().SequenceEqual(expected).Should().BeTrue();
    }

    [Fact]
    public void BoxesThatFollowOneAnotherInTheOrderAreNeighbours() {
        // One small box in each cube of a 16 x 16 x 16 lattice. Along a Hilbert curve every step
        // goes to a cube that shares a face with the last, so any run of the order is a compact
        // lump. (Along a Morton curve half the steps jump, some of them right across the lattice.)
        var boxes = new Box[16 * 16 * 16];
        for (var i = 0; i < boxes.Length; i++) {
            float x = i % 16 + 0.25f, y = i / 16 % 16 + 0.25f, z = i / 256 + 0.25f;
            boxes[i] = new Box(x, y, z, x + 0.5f, y + 0.5f, z + 0.5f);
        }
        var nodes = new BoxNode[boxes.Length];
        var order = new int[boxes.Length];
        BoxTree.Build(boxes, new Box(0, 0, 0, 16, 16, 16), nodes, order, new int[BoxTree.WorkspaceLength(boxes.Length)], out _).Should().BeTrue();

        for (var i = 1; i < order.Length; i++) {
            int a = order[i - 1], b = order[i];
            var steps = Math.Abs(a % 16 - b % 16) + Math.Abs(a / 16 % 16 - b / 16 % 16) + Math.Abs(a / 256 - b / 256);
            steps.Should().Be(1, $"boxes {a} and {b} follow one another");
        }
    }

    [Fact]
    public void TheSameBoxesGiveTheSameTreeByteForByte() {
        // Nine boxes scattered through a cube of 16.
        Box[] boxes = [
            new(1, 1, 1, 2, 2, 2), new(13, 2, 3, 15, 3, 4), new(6, 12, 1, 7, 14, 2), new(2, 3, 12, 3, 4, 13), new(12, 13, 12, 14, 14, 13),
            new(7, 7, 7, 9, 9, 9), new(3, 10, 9, 4, 11, 11), new(10, 4, 10, 11, 5, 12), new(14, 9, 2, 15, 11, 3),
        ];
        var nodes = new BoxNode[9];
        var order = new int[9];
        BoxTree.Build(boxes, new Box(0, 0, 0, 16, 16, 16), nodes, order, new int[BoxTree.WorkspaceLength(9)], out var nodeCount).Should().BeTrue();

        // The order, and the nodes as they lie in memory: six floats for the box, then First and Count.
        // If this changes, the order or the tree's layout changed. A baked block of shapes holds a
        // tree, so Shapes.Version must then be raised too.
        order.Should().Equal(0, 3, 6, 2, 8, 5, 4, 7, 1);
        Convert.ToHexString(MemoryMarshal.AsBytes(nodes.AsSpan(0, nodeCount))).Should().Be(
            "0000803F0000803F0000803F0000704100006041000050410200000000000000" +
            "0000803F0000803F0000803F0000E04000006041000050410000000004000000" +
            "0000E04000000040000000400000704100006041000050410400000000000000" +
            "0000E0400000E040000000400000704100003041000010410400000002000000" +
            "0000204100000040000040400000704100006041000050410600000003000000");
    }
}
