using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace AvaTerm.App;

/// <summary>A terminal surface with its scrollbar.</summary>
public sealed class TerminalView : Grid
{
    readonly ScrollBar _bar;
    bool _syncing;

    public TerminalControl Terminal { get; }

    public TerminalView(Settings settings, ColorTheme theme)
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        Terminal = new TerminalControl(settings, theme);
        _bar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Visibility = ScrollBarVisibility.Visible,
            AllowAutoHide = false,
            Focusable = false,
            Minimum = 0,
        };
        SetColumn(_bar, 1);
        Children.Add(Terminal);
        Children.Add(_bar);

        Terminal.ScrollChanged += SyncScrollBar;
        _bar.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            var (scrollback, _, _) = Terminal.GetScrollState();
            Terminal.ScrollToOffset(scrollback - (int)Math.Round(e.NewValue));
        };
    }

    void SyncScrollBar()
    {
        var (scrollback, offset, rows) = Terminal.GetScrollState();
        _syncing = true;
        _bar.Maximum = scrollback;
        _bar.ViewportSize = rows;
        _bar.LargeChange = Math.Max(1, rows - 1);
        _bar.SmallChange = 1;
        _bar.Value = scrollback - offset;
        _syncing = false;
    }
}

public sealed class MainWindow : Window
{
    sealed record Tab(TerminalView View, Border Header, TextBlock Title);

    static readonly bool IsMac = OperatingSystem.IsMacOS();

    readonly Settings _settings;
    readonly ColorTheme _theme = new();
    readonly List<Tab> _tabs = [];
    readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal };
    readonly Panel _host = new();
    readonly IBrush _activeBrush, _inactiveBrush;
    Tab? _active;

    public MainWindow(Settings settings)
    {
        _settings = settings;
        _activeBrush = Solid(_theme.Background);
        _inactiveBrush = Solid(_theme.Chrome);

        Title = "AvaTerm";
        Width = 980;
        Height = 640;
        MinWidth = 320;
        MinHeight = 200;
        Background = _activeBrush;

        var newTab = new Button
        {
            Content = "+",
            Focusable = false,
            Padding = new Thickness(12, 4),
            Margin = new Thickness(4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        newTab.Click += (_, _) => NewTab();

        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(_tabStrip);
        strip.Children.Add(newTab);
        var bar = new Border { Background = _inactiveBrush, Child = strip };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(_host);
        Content = root;

        // Tunnel so window shortcuts win over the terminal, which otherwise consumes every key.
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        Opened += (_, _) => NewTab();
        Closed += (_, _) =>
        {
            foreach (var tab in _tabs) tab.View.Terminal.Shutdown();
        };
    }

    static IBrush Solid(uint rgb) =>
        new ImmutableSolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));

    void OnShortcut(object? sender, KeyEventArgs e)
    {
        var m = e.KeyModifiers;
        bool chord = IsMac ? m == KeyModifiers.Meta : m == (KeyModifiers.Control | KeyModifiers.Shift);
        if (chord && e.Key == Key.T)
        {
            NewTab();
            e.Handled = true;
        }
        else if (chord && e.Key == Key.W)
        {
            if (_active != null) CloseTab(_active);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && (m & KeyModifiers.Control) != 0 && _tabs.Count > 1 && _active != null)
        {
            int step = (m & KeyModifiers.Shift) != 0 ? -1 : 1;
            int index = (_tabs.IndexOf(_active) + step + _tabs.Count) % _tabs.Count;
            Activate(_tabs[index]);
            e.Handled = true;
        }
    }

    void NewTab()
    {
        var view = new TerminalView(_settings, _theme);
        var title = new TextBlock
        {
            Text = "shell",
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Button
        {
            Content = "×",
            Focusable = false,
            Padding = new Thickness(6, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(title);
        content.Children.Add(close);
        var header = new Border { Padding = new Thickness(12, 6, 6, 6), Child = content };

        var tab = new Tab(view, header, title);
        header.PointerPressed += (_, _) => Activate(tab);
        close.Click += (_, _) => CloseTab(tab);
        view.Terminal.TitleChanged += text =>
        {
            title.Text = string.IsNullOrWhiteSpace(text) ? "shell" : text;
            if (_active == tab) Title = title.Text + " - AvaTerm";
        };
        view.Terminal.Exited += _ => CloseTab(tab);

        _tabs.Add(tab);
        _tabStrip.Children.Add(header);
        _host.Children.Add(view);
        Activate(tab);
    }

    void Activate(Tab tab)
    {
        _active = tab;
        foreach (var t in _tabs)
        {
            t.View.IsVisible = t == tab;
            t.Header.Background = t == tab ? _activeBrush : _inactiveBrush;
        }
        Title = (tab.Title.Text ?? "shell") + " - AvaTerm";
        // Focus after layout, once the newly shown terminal can take it.
        Dispatcher.UIThread.Post(() => tab.View.Terminal.Focus());
    }

    void CloseTab(Tab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0) return;
        _tabs.RemoveAt(index);
        _tabStrip.Children.Remove(tab.Header);
        _host.Children.Remove(tab.View);
        tab.View.Terminal.Shutdown();

        if (_tabs.Count == 0)
        {
            Close();
            return;
        }
        if (_active == tab) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }
}
