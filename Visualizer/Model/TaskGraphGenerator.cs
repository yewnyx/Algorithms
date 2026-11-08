namespace Yewnyx.Algorithms.Visualizer.Model;

public enum NodeRole : byte { Input, Compute, Output }

/// <param name="Count">Number of task graphs. They share no edges, so each becomes one task.</param>
/// <param name="LoneFraction">Share of task graphs that are a single node with no edges.</param>
/// <param name="MeanSize">Typical node count of a connected task graph. Sizes are heavy-tailed around it.</param>
/// <param name="MaxSize">Largest allowed task graph.</param>
/// <param name="PinnedFraction">Share of task graphs with a pinned node; up to the cap, these run every frame.</param>
/// <param name="Activity">Chance that a task has changed inputs when the scheduler visits it.</param>
public sealed record TaskGraphSpec(
    int Count, double LoneFraction, double MeanSize, int MaxSize,
    double PinnedFraction, double Activity, int Seed);

/// <param name="Role">Role of each node.</param>
/// <param name="Pinned">Whether each node asks for its task to run every frame.</param>
/// <param name="TruthTask">Generated task graph of each node, for checking the split.</param>
public sealed record GeneratedTasks(
    int NodeCount, int[] From, int[] To, NodeRole[] Role, bool[] Pinned,
    int[] TruthTask, int TaskCount, double Activity);

public static class TaskGraphGenerator {
    /// <summary>
    /// Builds many small dataflow graphs: input nodes feed compute nodes, compute nodes feed compute
    /// and output nodes. Node ids are shuffled so one graph's nodes are not contiguous.
    /// </summary>
    public static GeneratedTasks Generate(TaskGraphSpec spec, CancellationToken ct = default) {
        var rng = new Random(spec.Seed);
        var sizes = new int[spec.Count];
        var n = 0L;
        for (var c = 0; c < spec.Count; c++) {
            sizes[c] = rng.NextDouble() < spec.LoneFraction ? 1 : HeavyTailedSize(rng, spec.MeanSize, spec.MaxSize);
            n += sizes[c];
        }
        if (n > 4_000_000) { throw new ArgumentException($"{n:N0} nodes is too many; lower the count or sizes."); }

        var nodeCount = (int)n;
        var perm = new int[nodeCount];
        for (var i = 0; i < nodeCount; i++) { perm[i] = i; }
        for (var i = nodeCount - 1; i > 0; i--) {
            var j = rng.Next(i + 1);
            (perm[i], perm[j]) = (perm[j], perm[i]);
        }

        var role = new NodeRole[nodeCount];
        var pinned = new bool[nodeCount];
        var truth = new int[nodeCount];
        var from = new List<int>(nodeCount * 2);
        var to = new List<int>(nodeCount * 2);
        var outDegree = new int[spec.MaxSize + 1];
        var parent = new int[spec.MaxSize + 1];
        var local = new int[nodeCount];
        int Find(int x) {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }
        void Union(int a, int b) => parent[Find(a)] = Find(b);

        var next = 0;
        for (var c = 0; c < spec.Count; c++) {
            if ((c & 0xFFF) == 0) { ct.ThrowIfCancellationRequested(); }
            var size = sizes[c];
            var start = next;
            next += size;

            // Local layout: inputs, then compute nodes, then outputs.
            int inputs, outputs;
            if (size == 1) {
                inputs = rng.NextDouble() < 0.5 ? 1 : 0;
                outputs = 1 - inputs;
            } else {
                inputs = Math.Max(1, (int)Math.Round(size * 0.25));
                outputs = Math.Max(1, (int)Math.Round(size * 0.25));
                if (inputs + outputs > size) { outputs = size - inputs; }
            }
            var computes = size - inputs - outputs;
            var firstEdge = from.Count;
            for (var i = 0; i < size; i++) {
                var v = perm[start + i];
                local[v] = i;
                truth[v] = c;
                role[v] = i < inputs ? NodeRole.Input : i < inputs + computes ? NodeRole.Compute : NodeRole.Output;
            }
            if (rng.NextDouble() < spec.PinnedFraction) { pinned[perm[start + rng.Next(size)]] = true; }
            if (size == 1) { continue; }

            Array.Clear(outDegree, 0, size);
            void Link(int a, int b) {
                from.Add(perm[start + a]);
                to.Add(perm[start + b]);
                outDegree[a]++;
            }

            // Each compute node reads one or two earlier nodes; each output reads a compute node, or an input.
            for (var p = inputs; p < inputs + computes; p++) {
                var reads = 1 + (rng.NextDouble() < 0.4 ? 1 : 0);
                for (var k = 0; k < reads; k++) { Link(rng.Next(p), p); }
            }
            for (var a = inputs + computes; a < size; a++) {
                Link(computes > 0 ? inputs + rng.Next(computes) : rng.Next(inputs), a);
            }
            // An input nobody reads would be its own task, so wire it into something downstream.
            for (var s = 0; s < inputs; s++) {
                if (outDegree[s] == 0) { Link(s, inputs + rng.Next(size - inputs)); }
            }

            // Every node now reaches back to some input, but different inputs can root separate pieces.
            // Join each stray piece to input 0's piece with an edge from one of its inputs to a downstream
            // node of the main piece. An input has no incoming edges, so this cannot create a cycle.
            for (var i = 0; i < size; i++) { parent[i] = i; }
            for (var e = firstEdge; e < from.Count; e++) { Union(local[from[e]], local[to[e]]); }
            for (var s = 1; s < inputs; s++) {
                if (Find(s) == Find(0)) { continue; }
                int target;
                do { target = inputs + rng.Next(size - inputs); } while (Find(target) != Find(0));
                Link(s, target);
                Union(s, target);
            }
        }

        return new GeneratedTasks(nodeCount, from.ToArray(), to.ToArray(), role, pinned, truth, spec.Count, spec.Activity);
    }

    private static int HeavyTailedSize(Random rng, double mean, int max) {
        // Log-normal with sigma 0.9, centred so the mean lands near the requested size.
        const double sigma = 0.9;
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        var normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        var size = Math.Exp(Math.Log(Math.Max(2, mean)) - sigma * sigma / 2 + sigma * normal);
        return Math.Clamp((int)Math.Round(size), 2, Math.Max(2, max));
    }
}
