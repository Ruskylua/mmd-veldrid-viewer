using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace SimplePmxVeldrid;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(FindInitialModelPath(desktop.Args));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? FindInitialModelPath(string[]? args)
    {
        if (args is null) return null;
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || !File.Exists(arg))
                continue;

            var extension = Path.GetExtension(arg);
            if (extension.Equals(".pmx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".fbx", StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(arg);
        }

        return null;
    }
}
