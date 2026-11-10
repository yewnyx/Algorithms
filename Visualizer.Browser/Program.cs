using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;

[assembly: SupportedOSPlatform("browser")]

namespace Yewnyx.Algorithms.Visualizer;

internal static class Program {
    private static Task Main() =>
        AppBuilder.Configure<App>()
            .WithInterFont()
            .StartBrowserAppAsync("out");
}
