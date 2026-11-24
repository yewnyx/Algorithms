using FluentAssertions;

namespace Yewnyx.Spatial.Test;

// One grid's wall distance kept both ways from the same shapes, as a tree and as a dense map, so
// that what is asked of the tree can be held against what the dense map says. The dense map's counts
// are put down as the tree keeps them, to a fifteenth of the cap; Whole keeps them as marked.
internal sealed class TreeScene {
    public const float Unit = 1f / 128f;
    public const byte Cap = 128;

    public readonly GridShape Grid;
    public readonly WallNode[] Nodes;
    public readonly byte[] Bricks;
    public readonly byte[] Dense, Whole;

    public TreeScene(GridShape grid, in ShapeSet shapes) {
        Grid = grid;
        WallTrees.Measure(grid, shapes, Unit, Cap, out var mostNodes, out var mostBricks).Should().BeTrue();
        var nodes = new WallNode[mostNodes];
        var bricks = new byte[mostBricks * WallTrees.BrickBytes];
        WallTrees.Build(grid, shapes, Unit, Cap, nodes, bricks, out var nodeCount, out var brickCount).Should().BeTrue();
        Nodes = nodes[..nodeCount];
        Bricks = bricks[..(brickCount * WallTrees.BrickBytes)];
        Whole = new byte[grid.CellCount];
        for (var z = 0; z < grid.CountZ; z++) {
            WallDistance.MarkLayer(grid, shapes, Unit, Cap, z, Whole.AsSpan(z * grid.LayerLength, grid.LayerLength)).Should().BeTrue();
        }
        Dense = new byte[grid.CellCount];
        for (var i = 0; i < Dense.Length; i++) { Dense[i] = (byte)((Whole[i] * 15 / Cap * Cap + 14) / 15); }
    }

    public int BrickCount => Bricks.Length / WallTrees.BrickBytes;

    // What is left of a solid box when boxes are cut out of it: rooms and tunnels in rock.
    public static Solid[] Carved(Box world, params Box[] holes) {
        var left = new List<Box> { world };
        foreach (var hole in holes) {
            var next = new List<Box>();
            foreach (var box in left) {
                float x0 = Math.Max(box.MinX, hole.MinX), x1 = Math.Min(box.MaxX, hole.MaxX);
                float y0 = Math.Max(box.MinY, hole.MinY), y1 = Math.Min(box.MaxY, hole.MaxY);
                float z0 = Math.Max(box.MinZ, hole.MinZ), z1 = Math.Min(box.MaxZ, hole.MaxZ);
                if (x0 >= x1 || y0 >= y1 || z0 >= z1) {
                    next.Add(box);
                    continue;
                }
                if (box.MinX < x0) { next.Add(new Box(box.MinX, box.MinY, box.MinZ, x0, box.MaxY, box.MaxZ)); }
                if (x1 < box.MaxX) { next.Add(new Box(x1, box.MinY, box.MinZ, box.MaxX, box.MaxY, box.MaxZ)); }
                if (box.MinY < y0) { next.Add(new Box(x0, box.MinY, box.MinZ, x1, y0, box.MaxZ)); }
                if (y1 < box.MaxY) { next.Add(new Box(x0, y1, box.MinZ, x1, box.MaxY, box.MaxZ)); }
                if (box.MinZ < z0) { next.Add(new Box(x0, y0, box.MinZ, x1, y1, z0)); }
                if (z1 < box.MaxZ) { next.Add(new Box(x0, y0, z1, x1, y1, box.MaxZ)); }
            }
            left = next;
        }
        var solids = new Solid[left.Count];
        for (var i = 0; i < solids.Length; i++) { solids[i] = Solid.Box(left[i]); }
        return solids;
    }

    // A room of 10 m each way in rock, with a passage of 3 m from the middle of one wall to a second
    // room. For a body of 0.9 m the first room's open cells are one open cube of 16 cells, and the
    // only way out of it is by the cells in the middle of one face.
    public static Solid[] CubeRoom() => Carved(new Box(-2, -2, -2, 26, 26, 26),
        new Box(7, 7, 7, 17, 17, 17), new Box(16.9f, 10.5f, 10.5f, 21, 13.5f, 13.5f), new Box(20, 9, 9, 23.5f, 15, 15));

    public WallTree Tree => new(Grid, Unit, Cap, Nodes, Bricks);

    public WallMap Map => new(Grid, Unit, Cap, Dense);
}
