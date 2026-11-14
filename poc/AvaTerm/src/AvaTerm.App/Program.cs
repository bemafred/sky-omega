using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AvaTerm.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TerminalApp>().UsePlatformDetect().LogToTrace();
}

public sealed class TerminalApp : Application
{
    public override void Initialize()
    {
        Name = "AvaTerm";
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Settings.Load());
        base.OnFrameworkInitializationCompleted();
    }
}
