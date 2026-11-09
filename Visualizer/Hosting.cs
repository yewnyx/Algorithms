namespace Yewnyx.Algorithms.Visualizer;

/// <summary>What the host can do. The single-threaded browser build cannot start threads.</summary>
public static class Hosting {
#if SINGLE_THREADED
    public static bool ThreadsAvailable { get; } = false;
#else
    public static bool ThreadsAvailable { get; } = true;
#endif
}
