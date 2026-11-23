namespace Yewnyx.Spatial.Test;

// Ground packed into a heightfield and built into a set, with the surface that was kept read back
// from the blocks a plainer way to check against.
internal sealed class HeightScene {
    public readonly float[] Given;
    public readonly int CountU, CountW;
    public readonly float SizeU, SizeW;
    public readonly HeightField Field;
    private readonly HeightField[] _fields;
    public readonly uint[] Blocks;
    public readonly ushort[] Kept;
    public readonly Solid[] Solids;
    public readonly Box[] Boxes;
    public readonly BoxNode[] Nodes;
    public readonly int[] Order;
    public readonly int NodeCount;

    // The solid's faces as triangles in the heightfield's own axes: each block's surface, its four sides and its floor.
    private readonly List<((double u, double v, double w) a, (double u, double v, double w) b, (double u, double v, double w) c)> _faces = [];

    public HeightScene(Func<double, double, double> ground, int countU, int countW, float sizeU, float sizeW, float bound,
        Func<HeightField, HeightField>? place = null, Solid[]? solids = null) {
        CountU = countU; CountW = countW; SizeU = sizeU; SizeW = sizeW;
        Given = new float[(countU + 1) * (countW + 1)];
        for (var j = 0; j <= countW; j++) {
            for (var i = 0; i <= countU; i++) { Given[j * (countU + 1) + i] = (float)ground(i * (double)sizeU, j * (double)sizeW); }
        }
        Blocks = new uint[HeightFields.BlocksLength(countU, countW)];
        var kept = new ushort[HeightFields.HeightsLength(countU, countW)];
        if (!HeightFields.Pack(Given, countU, countW, sizeU, sizeW, bound, Blocks, kept, out var packed, out var keptCount)) {
            throw new InvalidOperationException("The ground was refused.");
        }
        Kept = kept[..keptCount];
        // Unless it is placed otherwise, the solid goes two metres below its lowest height.
        Field = place == null ? packed.Placed(0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1, packed.Lowest - 2) : place(packed);
        _fields = [Field];
        Solids = solids ?? [];
        var count = Solids.Length + 1;
        Boxes = new Box[count];
        Nodes = new BoxNode[Shapes.NodesLength(count)];
        Order = new int[count];
        if (!Shapes.Build(Solids, [], [], _fields, Blocks, Kept, Boxes, Nodes, Order, new int[Shapes.WorkspaceLength(count)], out NodeCount)) {
            throw new InvalidOperationException("The scene's shapes were refused.");
        }
        for (var blockW = 0; blockW < BlocksW; blockW++) {
            for (var blockU = 0; blockU < BlocksU; blockU++) { AddFaces(blockU, blockW); }
        }
    }

    public int BlocksU => (CountU + 15) / 16;
    public int BlocksW => (CountW + 15) / 16;

    public ShapeSet Set => new(Solids, [], [], _fields, Blocks, Kept, Boxes, Nodes.AsSpan(0, NodeCount), Order);

    public float Distance(float x, float y, float z, float limit = 100f) {
        if (!Shapes.Distance(Set, x, y, z, limit, out var distance)) { throw new InvalidOperationException("The query was refused."); }
        return distance;
    }

    // How many squares lie between one kept height of a block and the next.
    public int Spacing(int blockU, int blockW) => 1 << (int)(Blocks[blockW * BlocksU + blockU] & 3);

    // The ground as it was given, at a place in metres from the first corner: flat across each of a square's two triangles.
    public double GivenAt(double u, double w) {
        var (i, a) = Split(u / SizeU, CountU);
        var (j, b) = Split(w / SizeW, CountW);
        double At(int di, int dj) => Given[(j + dj) * (CountU + 1) + i + di];
        return Flat(At(0, 0), At(1, 0), At(0, 1), At(1, 1), a, b);
    }

    // The surface that one block kept, as a height in metres, at a place inside the block.
    public double KeptAt(int blockU, int blockW, double u, double w) {
        var spacing = Spacing(blockU, blockW);
        int width = Math.Min(16, CountU - blockU * 16), depth = Math.Min(16, CountW - blockW * 16), across = width / spacing + 1;
        var first = (int)(Blocks[blockW * BlocksU + blockU] >> 2);
        var (i, a) = Split((u / SizeU - blockU * 16) / spacing, width / spacing);
        var (j, b) = Split((w / SizeW - blockW * 16) / spacing, depth / spacing);
        double At(int di, int dj) => Field.Lowest + (double)Field.Step * Kept[first + (j + dj) * across + i + di];
        return Flat(At(0, 0), At(1, 0), At(0, 1), At(1, 1), a, b);
    }

    // The distance from a point in the frame to the solid, by a loop over every face in double: 0 inside a block's solid.
    public double Exact(double x, double y, double z) {
        double px = x - Field.X, py = y - Field.Y, pz = z - Field.Z;
        double u = px * Field.UX + py * Field.UY + pz * Field.UZ, v = px * Field.VX + py * Field.VY + pz * Field.VZ, w = px * Field.WX + py * Field.WY + pz * Field.WZ;
        for (var blockW = 0; blockW < BlocksW; blockW++) {
            for (var blockU = 0; blockU < BlocksU; blockU++) {
                double minU = blockU * 16 * (double)SizeU, maxU = Math.Min(CountU, blockU * 16 + 16) * (double)SizeU;
                double minW = blockW * 16 * (double)SizeW, maxW = Math.Min(CountW, blockW * 16 + 16) * (double)SizeW;
                if (u >= minU && u <= maxU && w >= minW && w <= maxW && v >= Field.Floor && v <= KeptAt(blockU, blockW, u, w)) { return 0; }
            }
        }
        var best = double.PositiveInfinity;
        foreach (var (a, b, c) in _faces) { best = Math.Min(best, ShapeScene.TriangleSquared(a, b, c, (u, v, w))); }
        return Math.Sqrt(best);
    }

    // Which square a place falls in and how far across it, with the far edge in the last square.
    private static (int, double) Split(double at, int count) {
        var whole = Math.Clamp((int)Math.Floor(at), 0, count - 1);
        return (whole, at - whole);
    }

    // A square is split from its corner at low U and low W to the one at high U and high W.
    private static double Flat(double h00, double h10, double h01, double h11, double a, double b) =>
        a >= b ? h00 + a * (h10 - h00) + b * (h11 - h10) : h00 + b * (h01 - h00) + a * (h11 - h01);

    private void AddFaces(int blockU, int blockW) {
        var spacing = Spacing(blockU, blockW);
        int width = Math.Min(16, CountU - blockU * 16), depth = Math.Min(16, CountW - blockW * 16);
        int across = width / spacing + 1, rows = depth / spacing + 1, first = (int)(Blocks[blockW * BlocksU + blockU] >> 2);
        double floor = Field.Floor;
        (double u, double v, double w) Top(int i, int j) =>
            ((blockU * 16 + i * spacing) * (double)SizeU, Field.Lowest + (double)Field.Step * Kept[first + j * across + i], (blockW * 16 + j * spacing) * (double)SizeW);
        (double u, double v, double w) Under(int i, int j) { var top = Top(i, j); return (top.u, floor, top.w); }
        void Side(int i0, int j0, int i1, int j1) {
            _faces.Add((Top(i0, j0), Top(i1, j1), Under(i1, j1)));
            _faces.Add((Top(i0, j0), Under(i1, j1), Under(i0, j0)));
        }
        for (var j = 0; j + 1 < rows; j++) {
            for (var i = 0; i + 1 < across; i++) {
                _faces.Add((Top(i, j), Top(i + 1, j), Top(i + 1, j + 1)));
                _faces.Add((Top(i, j), Top(i + 1, j + 1), Top(i, j + 1)));
            }
        }
        for (var i = 0; i + 1 < across; i++) { Side(i, 0, i + 1, 0); Side(i, rows - 1, i + 1, rows - 1); }
        for (var j = 0; j + 1 < rows; j++) { Side(0, j, 0, j + 1); Side(across - 1, j, across - 1, j + 1); }
        _faces.Add((Under(0, 0), Under(across - 1, 0), Under(across - 1, rows - 1)));
        _faces.Add((Under(0, 0), Under(across - 1, rows - 1), Under(0, rows - 1)));
    }
}
