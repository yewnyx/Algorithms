namespace Yewnyx.TestSupport;

/// <summary>Measures what a call allocates on the calling thread.</summary>
internal static class Allocation {
    /// <summary>
    /// The fewest bytes one call of <paramref name="action"/> allocated over <paramref name="calls"/> calls, after
    /// <paramref name="warmUp"/> calls that aren't counted. The runtime can count a few thousand bytes on this thread
    /// now and then (compiling or loading something while the machine is busy), and that hits one call, whereas an
    /// allocation of the code under test is in every call.
    /// </summary>
    public static long Fewest(Action action, int warmUp = 20, int calls = 50) {
        for (var i = 0; i < warmUp; i++) { action(); }
        var fewest = long.MaxValue;
        for (var i = 0; i < calls; i++) {
            var before = GC.GetAllocatedBytesForCurrentThread();
            action();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return fewest;
    }
}
