using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Yewnyx.Algorithms.Visualizer;

public partial class App : Application {
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.MainWindow = new Window {
                Title = "Algorithms Visualizer",
                Width = 1500, Height = 950,
                MinWidth = 900, MinHeight = 600,
                Content = new MainView(),
            };
        } else if (ApplicationLifetime is ISingleViewApplicationLifetime single) {
            single.MainView = new MainView();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
