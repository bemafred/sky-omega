using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace TicTacToe;

// A pristine, UNMODIFIED windowed Avalonia tic-tac-toe app — a realistic DEBUGGEE for ADR-012 Q8. It carries
// NO capture / debug instrumentation: a debug target must be visually debuggable AS-IS — the capture is the
// debugger's job, external to the target. Only DrHook's OWN visualizers are a special case (we own their
// rendering). The capture mechanism (OS window-capture by PID, or DrHook driving the framework's render APIs
// via func-eval) lives entirely outside this app.

internal sealed class Board : Grid
{
    public Board(string cells)
    {
        Width = 360;
        Height = 360;
        Background = Brushes.White;
        for (int i = 0; i < 3; i++)
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Star));
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        for (int r = 0; r < 3; r++)
        for (int c = 0; c < 3; c++)
        {
            char mark = cells[r * 3 + c];
            var cell = new Border
            {
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(2),
                Background = Brushes.White,
                Child = new TextBlock
                {
                    Text = mark == '.' ? "" : mark.ToString(),
                    FontSize = 96,
                    FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = mark == 'X' ? Brushes.RoyalBlue : Brushes.Crimson,
                },
            };
            SetRow(cell, r);
            SetColumn(cell, c);
            Children.Add(cell);
        }
    }
}

internal sealed class App : Application
{
    private static int _beat;

    public App() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A known mid-game board ('.' = empty):  X O X / . X O / O . X
            desktop.MainWindow = new Window
            {
                Title = "TicTacToe",
                Width = 380,
                Height = 400,
                Content = new Board("XOX.XOO.X"),
            };

            // A once-a-second beat on the UI thread, after the window is up and rendered — a natural breakpoint
            // site for the debugger. Just a game-loop tick; no capture, no debug awareness.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) => Beat();
            timer.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void Beat() => _beat++; // SNAPSHOT_HERE — a breakpoint site on the UI thread, window rendered
}

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
