using Xunit.Sdk;

namespace Yewnyx.TestSupport;

/// <summary>Measures what a call allocates on the calling thread.</summary>
internal static class Allocation {
    /// <summary>
    /// The fewest bytes one call of <paramref name="action"/> allocated over <paramref name="calls"/> calls, after
    /// <paramref name="warmUp"/> calls that aren't counted. For code that allocates a little in every call.
    /// </summary>
    public static long Fewest(Action action, int warmUp = 20, int calls = 50) => Measure(action, warmUp, calls).Fewest;

    /// <summary>
    /// Fails if more than <paramref name="tolerated"/> of <paramref name="calls"/> calls of <paramref name="action"/>
    /// allocate. The runtime can count a few thousand bytes on this thread now and then (compiling or loading something
    /// while the machine is busy), which is why a call or two is let off; an allocation of the code under test
    /// shows in many more.
    /// </summary>
    public static void AssertNone(Action action, int tolerated = 2, int warmUp = 20, int calls = 50) {
        var (fewest, most, allocating) = Measure(action, warmUp, calls);
        if (allocating > tolerated) {
            throw new XunitException($"Expected at most {tolerated} of {calls} calls to allocate, but {allocating} did (from {fewest} to {most} bytes).");
        }
    }

    private static (long Fewest, long Most, int Allocating) Measure(Action action, int warmUp, int calls) {
        for (var i = 0; i < warmUp; i++) { action(); }
        long fewest = long.MaxValue, most = 0;
        var allocating = 0;
        for (var i = 0; i < calls; i++) {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            fewest = Math.Min(fewest, bytes);
            most = Math.Max(most, bytes);
            if (bytes != 0) { allocating++; }
        }
        return (fewest, most, allocating);
    }
}
