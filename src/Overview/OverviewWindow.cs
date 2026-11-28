using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Overview;

/// <summary>The full-screen overview: blurred wallpaper, search bar, app results and live window thumbnails.</summary>
internal sealed class OverviewWindow : Window
{
    enum State { Hidden, Open, Closing }

    sealed class Tile
    {
        public Tile(WinItem win) => Win = win;
        public readonly WinItem Win;
        public IntPtr Thumb;
        public bool HasCrop;
        public Native.RECT Crop;
        public Size LayoutSize;
        public bool HasStart;          // real on-screen position on this monitor
        public Rect Start;
        public Rect From, Target, Current;
        public double FromOpacity, TargetOpacity, Opacity;
        public bool Visible = true;    // passes the search filter
        public double Score;
        public double SortKey;
        public string? Key;            // app usage key (see AppCatalog.KeyFor)
        public long Mru;               // higher = used more recently
        public int ZIndex;             // z-order position, tie-breaker for Mru
        public Border Ring = null!;
        public Border Label = null!;
        public Border CloseButton = null!;
        public Image Placeholder = null!;
        public Image LabelIcon = null!;
    }

    sealed class AppTile
    {
        public AppTile(AppEntry app, double score) { App = app; Score = score; }
        public readonly AppEntry App;
        public readonly double Score;
        public Border Root = null!;
        public Rect Bounds; // DIP, for the search-results row
        public bool InGrid; // lives in the scrollable app grid instead
        public bool IsWeb;  // the "Search the web" result
        public bool IsFile; // in the files row under the results
    }

    /// <summary>A web search suggestion, shown as a chip under the app results.</summary>
    sealed class Chip
    {
        public Chip(string text) => Text = text;
        public readonly string Text;
        public Border Root = null!;
        public Rect Bounds; // DIP
    }

    readonly List<Chip> _chips = new();
    readonly DispatcherTimer _suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    System.Threading.CancellationTokenSource? _suggestCts;
    const double ChipRowH = 34, ChipGap = 6, ChipMaxW = SearchWidth;
    const int MaxChips = 4;
    static bool SuggestionsOn => App.Settings.WebSuggestions;

    static readonly AppEntry WebEntry = new() { Name = "Search the web" };
    const double WebThreshold = 40; // weaker best match than this (substring-only or fuzzy) → web search is the default

    // Layout constants (DIP)
    const double SearchTop = 28, SearchHeight = 44, SearchWidth = 520;
    const double AppsTop = SearchTop + SearchHeight + 22;
    const double AppTileW = 104, AppTileH = 108, AppGap = 6;
    const double SideMargin = 56, BottomMargin = 36, Spacing = 34, LabelSpace = 40, RingPad = 5;
    const double OpenMs = 240, CloseMs = 180, FilterMs = 160;
    static int MaxAppResults => Math.Clamp(App.Settings.MaxAppResults, 3, 12) + 1; // + the web search tile

    static readonly Brush TextBrush = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    static readonly Brush DimTextBrush = Frozen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    static readonly Brush RingBrush = Frozen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    static readonly Brush LabelBrush = Frozen(Color.FromArgb(0x8C, 0x10, 0x10, 0x10));
    static readonly Brush LabelSelBrush = Frozen(Color.FromArgb(0xF0, 0x30, 0x30, 0x30));
    static readonly Brush AppSelBrush = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
    static readonly Brush CloseBrush = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    IntPtr _hwnd;
    State _state = State.Hidden;
    bool _inOpen;
    bool _allowRealClose;
    Native.RECT _mon;     // the overview's area: the monitor minus the taskbar
    Native.RECT _monFull; // the whole monitor
    double _dpi = 1;
    IntPtr _prevForeground;     // the window you were in when the overview opened
    IntPtr _prevForegroundRoot; // ...and its root owner

    bool WasCurrent(object? o) =>
        o is Tile t && t.Win.Hwnd != IntPtr.Zero &&
        (t.Win.Hwnd == _prevForeground || t.Win.Hwnd == _prevForegroundRoot);
    Tile? _activating;      // set while handing focus to a chosen window

    readonly Grid _root;
    readonly Border _dim;
    readonly Image _bg;      // blurred wallpaper
    readonly Image _bgSharp; // the wallpaper as the desktop shows it
    readonly Canvas _stage;
    readonly Border _searchBorder;
    readonly TextBox _search;
    readonly TextBlock _placeholder;
    readonly TextBlock _empty;

    readonly List<Tile> _tiles = new();
    readonly List<AppTile> _apps = new();
    readonly List<AppTile> _files = new();  // files and folders, a second row under the results
    object? _defaultSel;                     // what search selected by itself (files may replace it)
    readonly DispatcherTimer _fileTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };

    // App grid ("Show apps" button at the right end of the search bar)
    readonly Border _gridButton;
    readonly ScrollViewer _gridHost;
    readonly WrapPanel _gridPanel;
    readonly List<AppTile> _gridApps = new();
    IReadOnlyList<AppEntry>? _gridSource;
    bool _gridMode;
    static readonly Brush GridButtonHover = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    static readonly Brush GridButtonOn = Frozen(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF));
    object? _sel;
    bool _suppressText;
    bool _hoverLocked;
    Native.POINT _lockCursor; // physical cursor position when hover was locked
    string? _bgKey;

    // animation state
    long _animStart;
    double _animMs;
    Action? _animDone;
    bool _rendering;
    bool _fadeBackground = true; // set on open: was the desktop background visible?

    /// <summary>
    /// True if a meaningful part of the desktop background was visible when the overview opened
    /// (not covered by windows). Samples a grid over the screen against where the windows are.
    /// </summary>
    bool BackgroundVisible()
    {
        var frames = _tiles.Where(t => t.HasStart).Select(t => t.Start).ToList();
        if (frames.Count == 0) return true;
        const int cols = 48, rows = 27;
        double w = _mon.Width, h = _mon.Height;
        int uncovered = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var p = new Point((c + 0.5) * w / cols, (r + 0.5) * h / rows);
                if (!frames.Any(f => f.Contains(p))) uncovered++;
            }
        }
        return uncovered > cols * rows / 100; // more than ~1 % of the screen
    }
    double _chrome, _chromeFrom, _chromeTo;

    public OverviewWindow()
    {
        Title = "Overview";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent; // the window is glass-backed; _root paints the actual background
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        // Two wallpaper layers: the sharp one looks exactly like the desktop; the blurred one fades
        // in over it when the overview opens (and out when it closes), so the blur is a transition.
        _bgSharp = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapScalingMode(_bgSharp, BitmapScalingMode.HighQuality);
        _bg = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapScalingMode(_bg, BitmapScalingMode.Linear);

        _dim = new Border { Background = Frozen(Color.FromArgb(0x5C, 0, 0, 0)) };

        _stage = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        _stage.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!OpenKeyboardSelection()) Dismiss(null);
        };

        // Search bar
        _placeholder = new TextBlock
        {
            Text = "Type to search",
            Foreground = DimTextBrush,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _search = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            SelectionBrush = Frozen(Color.FromArgb(0x80, 0x35, 0x84, 0xE4)),
            FontSize = 16,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null
        };
        _search.TextChanged += (_, _) =>
        {
            _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_suppressText) OnQueryChanged();
        };
        var glyph = new TextBlock
        {
            Text = "",
            FontFamily = IconFont,
            FontSize = 15,
            Foreground = DimTextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18, 0, 12, 0)
        };
        var inner = new Grid { Margin = new Thickness(0, 0, 18, 0) };
        inner.Children.Add(_placeholder);
        inner.Children.Add(_search);
        // "Show apps" button: GNOME's 3x3 dots, drawn with shapes so it never depends on an icon font
        var dots = new System.Windows.Controls.Primitives.UniformGrid { Rows = 3, Columns = 3, Width = 14, Height = 14, IsHitTestVisible = false };
        for (int i = 0; i < 9; i++)
            dots.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 3.2,
                Height = 3.2,
                Fill = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        _gridButton = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = Brushes.Transparent,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Show apps",
            Child = dots
        };
        _gridButton.MouseEnter += (_, _) => UpdateGridButton();
        _gridButton.MouseLeave += (_, _) => UpdateGridButton();
        _gridButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_gridMode) HideGrid(); else ShowGrid();
        };

        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(glyph, 0);
        Grid.SetColumn(inner, 1);
        Grid.SetColumn(_gridButton, 2);
        bar.Children.Add(glyph);
        bar.Children.Add(inner);
        bar.Children.Add(_gridButton);

        // The app grid itself: a vertically scrolling, centred wrap of app tiles.
        _gridPanel = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 24) };
        _gridHost = new ScrollViewer
        {
            Content = _gridPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Background = Brushes.Transparent,
            Margin = new Thickness(SideMargin, AppsTop, SideMargin, BottomMargin),
            Visibility = Visibility.Collapsed
        };
        SmoothScroll.Attach(_gridHost);
        // A click on empty space in the grid goes back to the windows.
        _gridHost.MouseLeftButtonUp += (_, e) => { e.Handled = true; HideGrid(); };
        _searchBorder = new Border
        {
            Width = SearchWidth,
            Height = SearchHeight,
            CornerRadius = new CornerRadius(SearchHeight / 2),
            Background = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderBrush = Frozen(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, SearchTop, 0, 0),
            Child = bar
        };
        _searchBorder.MouseLeftButtonDown += (_, e) => { _search.Focus(); e.Handled = true; };

        _empty = new TextBlock
        {
            Text = "No results",
            Foreground = DimTextBrush,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        _root = new Grid { Background = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1C)) };
        _root.Children.Add(_bgSharp);
        _root.Children.Add(_bg);
        _root.Children.Add(_dim);
        _root.Children.Add(_stage);
        _root.Children.Add(_gridHost);
        _root.Children.Add(_searchBorder);
        _root.Children.Add(_empty);
        Content = _root;

        _suggestTimer.Tick += (_, _) => { _suggestTimer.Stop(); FetchSuggestions(); };
        _fileTimer.Tick += (_, _) => { _fileTimer.Stop(); FetchFiles(); };
        // A newly installed app: show it in the results you're looking at, without typing again.
        AppCatalog.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_state == State.Open && !_gridMode && Searching) OnQueryChanged();
        });

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseWheel += OnPreviewMouseWheel;
        Deactivated += (_, _) => { if (!_inOpen && _activating == null && _state == State.Open) Dismiss(null); };
    }

    static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ------------------------------------------------------------------ lifecycle

    public void Prepare()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        int on = 1, noRound = 1;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_TRANSITIONS_FORCEDISABLED, ref on, sizeof(int)); // no fade on show/hide
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(int));
        KeepWindowsBehindLive();
    }

    /// <summary>
    /// Called once startup is done and the app is idle: blur the wallpaper and run WPF's first
    /// layout pass (templates, fonts), so the first open is as fast as later ones without
    /// slowing down startup itself.
    /// </summary>
    public void WarmUp()
    {
        if (_state != State.Hidden) return; // already opened: nothing left to warm up
        MenuStyles.Install();
        try
        {
            UpdateBackground();
            _root.Measure(new Size(1920, 1080));
            _root.Arrange(new Rect(0, 0, 1920, 1080));
        }
        catch (Exception e) { Log.Write(e); }
    }

    /// <summary>
    /// Many apps stop drawing when they think they're fully covered, which would freeze their
    /// thumbnails behind this full-screen window. Two things stop them from seeing it as a cover:
    /// 1. WS_EX_TOOLWINDOW: Chromium (Chrome, Edge, Brave, Discord, Spotify, VS Code, Steam…) and
    ///    Firefox skip tool windows in their occlusion checks.
    /// 2. A glass-backed surface: DWM treats a window with an alpha surface as see-through, so the
    ///    windows behind aren't marked occluded (helps games and video players).
    /// Minimized windows can't be live: Windows doesn't let them draw at all.
    /// </summary>
    void KeepWindowsBehindLive()
    {
        try
        {
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        }
        catch (Exception e) { Log.Write(e); }

        try
        {
            var source = HwndSource.FromHwnd(_hwnd);
            if (source?.CompositionTarget != null) source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var glass = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            Native.DwmExtendFrameIntoClientArea(_hwnd, ref glass);
        }
        catch (Exception e) { Log.Write(e); }
    }

    public void AllowRealClose() => _allowRealClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 just dismisses; the app quits from the tray.
        if (!_allowRealClose)
        {
            e.Cancel = true;
            Dismiss(null);
        }
        base.OnClosing(e);
    }

    public void Toggle()
    {
        if (_state == State.Hidden) Open();
        else if (_state == State.Closing)
        {
            // Tapped again while the close animation is still running (fast back-and-forth
            // switching): finish closing at once and open again, instead of ignoring the tap.
            ForceHide();
            Open();
        }
        else if (_state == State.Open)
        {
            // Closing with the Windows key switches to the highlighted window, so a quick
            // double-tap jumps back to the previous window like Alt+Tab. Esc closes without switching.
            if (App.Settings.SecondTapSwitches && _sel is Tile t && !_gridMode)
            {
                Log.Write($"switch: '{t.Win.AppName}' via {(_hoverLocked ? "keyboard/default selection" : "mouse hover")}");
                ActivateTile(t);
            }
            else Dismiss(null);
        }
    }

    /// <summary>Highlights the window you used before the current one (or the most recent one
    /// if no window had focus), like Alt+Tab does when it opens.</summary>
    void SelectDefault()
    {
        var recent = RecentWindows();
        if (recent.Count == 0) { Select(null); return; }
        Select(WasCurrent(recent[0]) && recent.Count > 1 ? recent[1] : recent[0]);
    }

    void Open()
    {
        _inOpen = true;
        try
        {
            _state = State.Open;
            // Remember both the focused window and its root owner: a dialog's tile is its owner,
            // and some apps' main windows are owned by a hidden helper window.
            _prevForeground = Native.GetForegroundWindow();
            _prevForegroundRoot = Native.GetAncestor(_prevForeground, 3 /*GA_ROOTOWNER*/);
            Native.GetCursorPos(out var cursor);
            var monitor = Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
            _monFull = Native.MonitorRect(monitor);
            // Cover only the work area, so the taskbar stays visible and clickable.
            _mon = Native.MonitorWorkRect(monitor);
            if (_mon.Width <= 0 || _mon.Height <= 0) _mon = _monFull;
            PrepareTaskbar(monitor); // keep it visible; with auto-hide on, also make room for it
            int mw = _mon.Width, mh = _mon.Height;

            var items = WindowEnumerator.Enumerate(_hwnd, loadIcons: false); // missing icons load async

            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _mon.Left, _mon.Top, mw, mh, Native.SWP_NOACTIVATE);
            UpdateBackground();
            ApplyAppearance();

            _suppressText = true;
            _search.Text = "";
            _suppressText = false;
            _sel = null;
            _wheelAccum = 0;
            _wheelSelected = false;
            _chrome = 0;
            ApplyChrome();
            ClearTiles();
            ClearApps();
            ClearFiles();
            _empty.Visibility = Visibility.Collapsed;

            Show();
            // WPF may resize the window when it lands on a monitor with a different DPI: pin it again.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _mon.Left, _mon.Top, mw, mh, 0);
            Native.ForceForeground(_hwnd);
            Activate();
            RaiseTaskbar(); // activating the overview put it above the taskbar
            _search.Focus();
            Keyboard.Focus(_search);
            UpdateLayout();
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            PositionWallpaper();

            BuildTiles(items);
            _fadeBackground = BackgroundVisible();
            LockHover();
            Relayout();

            foreach (var t in _tiles)
            {
                if (t.HasStart) { t.Current = t.Start; t.Opacity = 1; }
                else { t.Current = Shrink(t.Target, 0.85); t.Opacity = 0; }
            }
            SelectDefault();
            StartAnim(OpenMs, 1, null);

            var order = RecentWindows().Take(4).Select(x => x.Win.AppName).ToList();
            Log.Write($"open: was in '{(_tiles.FirstOrDefault(x => WasCurrent(x))?.Win.AppName ?? Native.ClassName(_prevForeground))}', " +
                      $"selected '{(_sel as Tile)?.Win.AppName}', recent: {string.Join(" > ", order)}");
        }
        catch (Exception e)
        {
            Log.Write(e);
            ForceHide();
        }
        finally
        {
            _inOpen = false;
        }
    }

    void Dismiss(Tile? activated)
    {
        if (_state != State.Open) return;
        _state = State.Closing;
        _taskbarTimer.Stop();
        CloseMenu();
        foreach (var t in _tiles)
        {
            bool backToPlace = t.HasStart && !(t == activated && t.Win.Minimized);
            if (backToPlace) { t.Target = t.Start; t.TargetOpacity = 1; }
            else { t.Target = Shrink(t.Current, 0.85); t.TargetOpacity = 0; }
        }
        // The window you switch to ends up in front, so draw it in front while it glides back too.
        if (activated != null) RaiseThumbnail(activated);
        StartAnim(CloseMs, 0, FinishHide);
    }

    /// <summary>Moves a tile's live thumbnail above all others by registering a fresh one
    /// (new first, then the old one goes, so it never blinks).</summary>
    void RaiseThumbnail(Tile t)
    {
        Panel.SetZIndex(t.Ring, 1);
        Panel.SetZIndex(t.Label, 1);
        if (t.Thumb == IntPtr.Zero) return;
        if (Native.DwmRegisterThumbnail(_hwnd, t.Win.Hwnd, out var fresh) != 0) return;
        var old = t.Thumb;
        t.Thumb = fresh;
        ApplyTile(t);
        Native.DwmUnregisterThumbnail(old);
    }

    // ------------------------------------------------------------------ taskbar

    IntPtr _taskbar;           // the taskbar on the overview's monitor, kept above the overview
    bool _taskbarWasTopmost;   // false when a fullscreen app had pushed it behind itself
    bool _cameFromFullscreen;  // the overview was opened from a fullscreen app
    int _taskbarEdge;          // 0 left, 1 top, 2 right, 3 bottom (auto-hide only)
    int _taskbarThickness;     // physical pixels (auto-hide only)
    Thickness _taskbarInset;   // physical pixels the window grid stays clear of (auto-hide taskbar on top)
    DispatcherTimer _taskbarTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    /// <summary>
    /// Keeps the taskbar visible above the overview while it's open. Two cases need help:
    /// <list type="bullet">
    /// <item><b>A fullscreen app was in front</b> (game, video, F11 browser): Windows pushes the taskbar
    /// behind it, and doesn't bring it back for the overview (Explorer ignores tool windows like this
    /// one). So the taskbar is raised above the overview, and put behind the fullscreen app again
    /// when you go back to it.</item>
    /// <item><b>Auto-hide is on:</b> the overview covers the whole screen but keeps the taskbar's strip
    /// free, and keeps the taskbar (including its hover edge) above itself, so pointing at the bottom
    /// slides it up. Windows 11 gives apps no clean way to show it unasked.</item>
    /// </list>
    /// </summary>
    void PrepareTaskbar(IntPtr monitor)
    {
        _taskbar = IntPtr.Zero;
        _taskbarInset = new Thickness(0);
        _taskbarTimer.Stop();
        var tb = Native.TaskbarOnMonitor(monitor);
        if (tb == IntPtr.Zero) return;
        _taskbar = tb;
        _taskbarWasTopmost = (Native.GetWindowLongPtr(tb, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0;
        _cameFromFullscreen = Native.IsForegroundFullscreen(); // the app is still in front at this point

        if (Native.IsTaskbarAutoHide() && Native.GetWindowRect(tb, out var r))
        {
            var m = _monFull;
            bool horizontal = r.Width >= r.Height;
            int thickness = horizontal ? r.Height : r.Width;
            if (thickness > 0 && thickness <= (horizontal ? m.Height : m.Width) / 3)
            {
                // A hidden taskbar sits just past its screen edge, so its centre tells which edge that is.
                _taskbarEdge = horizontal
                    ? ((r.Top + r.Bottom) / 2 > (m.Top + m.Bottom) / 2 ? 3 : 1)
                    : ((r.Left + r.Right) / 2 > (m.Left + m.Right) / 2 ? 2 : 0);
                _taskbarThickness = thickness;

                // Cover the whole screen, including behind the taskbar. The taskbar is see-through, so if
                // the overview stopped short of it, it would show the (maybe bright) app window behind it
                // instead of the overview's background. Windows are kept clear of it with an inset instead.
                _mon = m;
                switch (_taskbarEdge)
                {
                    case 0: _taskbarInset.Left = thickness; break;
                    case 1: _mon.Top = m.Top + thickness; break; // top: the search bar must stay below it
                    case 2: _taskbarInset.Right = thickness; break;
                    default: _taskbarInset.Bottom = thickness; break;
                }
            }
        }

        _taskbarTimer.Tick -= OnTaskbarTimer;
        _taskbarTimer.Tick += OnTaskbarTimer;
        _taskbarTimer.Start();
        if (_cameFromFullscreen || !_taskbarWasTopmost)
            Log.Write($"opened from fullscreen (taskbar topmost: {_taskbarWasTopmost}): taskbar raised above the overview");
    }

    /// <summary>Only changes z-order, never the taskbar's position or state, so Explorer's own
    /// show/hide on hover keeps working undisturbed.</summary>
    void OnTaskbarTimer(object? sender, EventArgs e)
    {
        if (_state != State.Open || _taskbar == IntPtr.Zero) { _taskbarTimer.Stop(); return; }
        if (!Native.IsAbove(_taskbar, _hwnd)) RaiseTaskbar();
    }

    /// <summary>Puts the taskbar above the overview (activating the overview puts it on top).</summary>
    void RaiseTaskbar()
    {
        if (_taskbar == IntPtr.Zero) return;
        Native.SetWindowPos(_taskbar, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// After closing: if a fullscreen app had pushed the taskbar behind itself and you're back in a
    /// fullscreen app now, put the taskbar behind it again, so it doesn't sit on top of your game or
    /// video. In a normal window the taskbar belongs on top, so then it's left as it is.
    /// </summary>
    void RestoreTaskbar()
    {
        var tb = _taskbar;
        _taskbar = IntPtr.Zero;
        if (tb == IntPtr.Zero || (!_cameFromFullscreen && _taskbarWasTopmost)) return;
        PutTaskbarBehindFullscreen(tb);
        // The window you switched to may take a moment to come to the front: check once more.
        var again = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        again.Tick += (_, _) => { again.Stop(); if (_state == State.Hidden) PutTaskbarBehindFullscreen(tb); };
        again.Start();
    }

    void PutTaskbarBehindFullscreen(IntPtr tb)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _hwnd || !Native.IsForegroundFullscreen()) return;
        if (!Native.IsAbove(tb, fg)) return; // already behind it
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        Native.SetWindowPos(tb, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        Native.SetWindowPos(tb, fg, 0, 0, 0, 0, flags); // just behind the fullscreen app
        Log.Write("back in a fullscreen app: taskbar put behind it again");
    }

    void FinishHide()
    {
        StopRendering();
        Hide();
        _taskbarTimer.Stop();
        RestoreTaskbar();
        Select(null); // also clears the highlight on a reused app-grid tile
        if (_gridMode) LeaveGrid();
        ClearChips();
        ClearTiles();
        ClearApps();
        ClearFiles();
        _sel = null;
        _state = State.Hidden;
        AppCatalog.RefreshIfStale();
    }

    public void ForceHide()
    {
        try { FinishHide(); } catch (Exception e) { Log.Write(e); }
    }

    /// <summary>Settings that change how the overview looks, applied each time it opens.</summary>
    void ApplyAppearance()
    {
        byte alpha = (byte)Math.Round(Math.Clamp(App.Settings.DimPercent, 0, 80) * 2.55);
        if (_dim.Background is not SolidColorBrush b || b.Color.A != alpha)
            _dim.Background = Frozen(Color.FromArgb(alpha, 0, 0, 0));
    }

    // ------------------------------------------------------------------ background

    enum WallpaperFit { Fill, Fit, Stretch, Center }
    WallpaperFit _wallFit = WallpaperFit.Fill;

    /// <summary>How Windows fits the wallpaper (Settings → Personalization → Background).</summary>
    static WallpaperFit ReadWallpaperFit()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            string style = k?.GetValue("WallpaperStyle") as string ?? "10";
            string tile = k?.GetValue("TileWallpaper") as string ?? "0";
            if (tile == "1") return WallpaperFit.Center; // tiled: shown centred, close enough
            return style switch
            {
                "6" => WallpaperFit.Fit,
                "2" => WallpaperFit.Stretch,
                "0" => WallpaperFit.Center,
                _ => WallpaperFit.Fill // "10" fill, "22" span
            };
        }
        catch { return WallpaperFit.Fill; }
    }

    void UpdateBackground()
    {
        try
        {
            var sb = new StringBuilder(1024);
            Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
            string path = sb.ToString();
            var fit = ReadWallpaperFit();
            uint c = Native.GetSysColor(1); // COLOR_DESKTOP (0x00BBGGRR): the colour around a fitted/centred picture
            string key = (File.Exists(path) ? path + "|" + File.GetLastWriteTimeUtc(path).Ticks : "") + "|" + fit + "|" + c;
            if (key == _bgKey) return;
            _bgKey = key;
            _wallFit = fit;
            _root.Background = Frozen(Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF)));

            if (!File.Exists(path))
            {
                _bg.Source = null;
                _bgSharp.Source = null;
                return;
            }
            _bgSharp.Source = SharpWallpaper(path);
            _bg.Source = BlurredWallpaper(path);
        }
        catch (Exception e)
        {
            Log.Write(e);
            _bg.Source = null;
            _bgSharp.Source = null;
        }
    }

    /// <summary>The wallpaper at screen resolution (capped at 4K wide, so huge photos don't eat memory).</summary>
    static BitmapSource SharpWallpaper(string path)
    {
        int widest = 1920;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens) widest = Math.Max(widest, screen.Bounds.Width);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path);
        bi.DecodePixelWidth = Math.Min(widest, 3840);
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>
    /// Lays both wallpaper layers over the whole monitor exactly as Windows does (the overview window
    /// may stop at the taskbar, so the monitor can start outside it), so the sharp layer lines up
    /// with the real desktop and the open looks seamless.
    /// </summary>
    void PositionWallpaper()
    {
        double d = _dpi;
        double left = (_monFull.Left - _mon.Left) / d, top = (_monFull.Top - _mon.Top) / d;
        double w = _monFull.Width / d, h = _monFull.Height / d;

        foreach (var layer in new[] { _bgSharp, _bg })
        {
            layer.Width = w;
            layer.Height = h;
            layer.Margin = new Thickness(left, top, 0, 0);
        }
        // The blurred layer always fills; the sharp one copies the desktop's fit mode.
        _bg.Stretch = Stretch.UniformToFill;
        switch (_wallFit)
        {
            case WallpaperFit.Fit: _bgSharp.Stretch = Stretch.Uniform; break;
            case WallpaperFit.Stretch: _bgSharp.Stretch = Stretch.Fill; break;
            case WallpaperFit.Center when _bgSharp.Source is BitmapSource src:
                // Shown at its own pixel size, centred.
                double iw = src.PixelWidth / d, ih = src.PixelHeight / d;
                _bgSharp.Stretch = Stretch.Fill;
                _bgSharp.Width = iw;
                _bgSharp.Height = ih;
                _bgSharp.Margin = new Thickness(left + (w - iw) / 2, top + (h - ih) / 2, 0, 0);
                break;
            default: _bgSharp.Stretch = Stretch.UniformToFill; break;
        }
    }

    /// <summary>Decodes the wallpaper tiny and box-blurs it in software; upscaling then gives a smooth, cheap blur.</summary>
    static BitmapSource BlurredWallpaper(string path)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri(path);
        bi.DecodePixelWidth = 320;
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bi.EndInit();
        var conv = new FormatConvertedBitmap(bi, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(px, w, h, 5, horizontal: true);
            BoxBlur(px, w, h, 5, horizontal: false);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }

    static void BoxBlur(byte[] px, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h;
        var tmp = new byte[len * 4];
        int win = r * 2 + 1;
        for (int line = 0; line < lines; line++)
        {
            int Index(int i) => horizontal ? (line * w + i) * 4 : (i * w + line) * 4;
            int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            for (int k = -r; k <= r; k++)
            {
                int j = Index(Math.Clamp(k, 0, len - 1));
                s0 += px[j]; s1 += px[j + 1]; s2 += px[j + 2]; s3 += px[j + 3];
            }
            for (int i = 0; i < len; i++)
            {
                tmp[i * 4] = (byte)(s0 / win);
                tmp[i * 4 + 1] = (byte)(s1 / win);
                tmp[i * 4 + 2] = (byte)(s2 / win);
                tmp[i * 4 + 3] = (byte)(s3 / win);
                int add = Index(Math.Min(i + r + 1, len - 1));
                int rem = Index(Math.Max(i - r, 0));
                s0 += px[add] - px[rem]; s1 += px[add + 1] - px[rem + 1];
                s2 += px[add + 2] - px[rem + 2]; s3 += px[add + 3] - px[rem + 3];
            }
            for (int i = 0; i < len; i++)
            {
                int j = Index(i);
                px[j] = tmp[i * 4]; px[j + 1] = tmp[i * 4 + 1]; px[j + 2] = tmp[i * 4 + 2]; px[j + 3] = tmp[i * 4 + 3];
            }
        }
    }

    // ------------------------------------------------------------------ tiles

    void BuildTiles(List<WinItem> items)
    {
        // Back to front: DWM draws thumbnails in the order they're registered (last on top),
        // so registering the bottom window first keeps the real stacking order while they glide.
        for (int index = items.Count - 1; index >= 0; index--)
        {
            var w = items[index];
            var t = new Tile(w) { ZIndex = index, Mru = Mru.Of(w.Hwnd), Key = AppCatalog.KeyFor(w.ExePath, w.Aumid) };

            if (Native.DwmRegisterThumbnail(_hwnd, w.Hwnd, out var thumb) == 0)
            {
                t.Thumb = thumb;
                Native.DwmQueryThumbnailSourceSize(thumb, out var src);
                if (!w.Minimized && src.cx > 0 && src.cy > 0 && w.WindowRect.Width > 0 && w.WindowRect.Height > 0)
                {
                    // Crop away the invisible resize borders so the thumbnail is exactly the visible window.
                    double sx = src.cx / (double)w.WindowRect.Width, sy = src.cy / (double)w.WindowRect.Height;
                    t.Crop = new Native.RECT(
                        (int)Math.Round((w.Frame.Left - w.WindowRect.Left) * sx),
                        (int)Math.Round((w.Frame.Top - w.WindowRect.Top) * sy),
                        (int)Math.Round((w.Frame.Right - w.WindowRect.Left) * sx),
                        (int)Math.Round((w.Frame.Bottom - w.WindowRect.Top) * sy));
                    t.HasCrop = t.Crop.Width > 0 && t.Crop.Height > 0;
                }
                if (w.Minimized && src.cx >= 200 && src.cy >= 120)
                    t.LayoutSize = new Size(src.cx, src.cy);
            }

            if (t.LayoutSize.Width <= 0)
                t.LayoutSize = new Size(Math.Max(w.Frame.Width, 160), Math.Max(w.Frame.Height, 100));
            if (t.LayoutSize.Width < 160 || t.LayoutSize.Height < 100)
                t.LayoutSize = new Size(Math.Max(t.LayoutSize.Width, 160), Math.Max(t.LayoutSize.Height, 100));

            bool onThisMonitor = w.Frame.Right > _monFull.Left && w.Frame.Left < _monFull.Right &&
                                 w.Frame.Bottom > _monFull.Top && w.Frame.Top < _monFull.Bottom;
            if (!w.Minimized && onThisMonitor)
            {
                t.HasStart = true;
                t.Start = new Rect(w.Frame.Left - _mon.Left, w.Frame.Top - _mon.Top, w.Frame.Width, w.Frame.Height);
            }

            // Reading order of where the windows actually are, so they glide naturally.
            double cy = (w.Frame.Top + w.Frame.Bottom) / 2.0, cx = (w.Frame.Left + w.Frame.Right) / 2.0;
            t.SortKey = Math.Round(cy / 200) * 100000 + cx;

            CreateTileVisuals(t);
            _tiles.Add(t);
        }

        // Layout order: where windows are on screen; ties go to the window higher in the z-order.
        var ordered = _tiles.OrderBy(t => t.SortKey).ThenBy(t => t.ZIndex).ToList();
        _tiles.Clear();
        _tiles.AddRange(ordered);
    }

    void CreateTileVisuals(Tile t)
    {
        t.Placeholder = new Image
        {
            Source = t.Win.Icon,
            Width = 64,
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = t.Thumb == IntPtr.Zero ? Visibility.Visible : Visibility.Collapsed
        };
        t.Ring = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(3),
            BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent,
            Child = t.Placeholder
        };

        var icon = new Image { Source = t.Win.Icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        t.LabelIcon = icon;
        if (t.Win.Icon == null)
        {
            // Not cached yet: load it in the background and fill it in when it arrives.
            var w = t.Win;
            string key = "window|" + (w.Aumid ?? w.ExePath ?? w.Hwnd.ToString());
            IconLoader.LoadAsync(key, () => WindowEnumerator.LoadIcon(w), loaded =>
            {
                if (loaded == null) return;
                w.Icon = loaded;
                t.Placeholder.Source = loaded;
                t.LabelIcon.Source = loaded;
            });
        }
        var title = new TextBlock
        {
            Text = t.Win.Title,
            Foreground = TextBrush,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        t.CloseButton = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = CloseBrush,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Close window",
            Child = new TextBlock
            {
                Text = "",
                FontFamily = IconFont,
                FontSize = 9,
                Foreground = TextBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(t.CloseButton, Dock.Right);
        dock.Children.Add(icon);
        dock.Children.Add(t.CloseButton);
        dock.Children.Add(title);
        t.Label = new Border
        {
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(9, 4, 9, 4),
            Background = LabelBrush,
            Child = dock
        };

        MouseEventHandler enter = (_, _) => { if (!_hoverLocked) Select(t); };
        MouseButtonEventHandler click = (_, e) => { e.Handled = true; if (!OpenKeyboardSelection()) ActivateTile(t); };
        MouseButtonEventHandler middle = (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            CloseWindow(t);
        };
        t.Ring.MouseEnter += enter;
        t.Label.MouseEnter += enter;
        t.Ring.MouseLeftButtonUp += click;
        t.Label.MouseLeftButtonUp += click;
        t.Ring.MouseUp += middle;
        t.Label.MouseUp += middle;
        t.CloseButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseWindow(t); };
        MouseButtonEventHandler rightClick = (_, e) => { e.Handled = true; ShowWindowMenu(t, atMouse: true); };
        t.Ring.MouseRightButtonUp += rightClick;
        t.Label.MouseRightButtonUp += rightClick;

        _stage.Children.Add(t.Ring);
        _stage.Children.Add(t.Label);
    }

    void ClearTiles()
    {
        foreach (var t in _tiles)
        {
            if (t.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(t.Thumb);
            t.Thumb = IntPtr.Zero;
            _stage.Children.Remove(t.Ring);
            _stage.Children.Remove(t.Label);
        }
        _tiles.Clear();
    }

    void RemoveTile(Tile t)
    {
        if (!_tiles.Contains(t)) return;
        // If the closed window was selected, move the selection to the next one, so
        // Backspace / Del can be pressed repeatedly to close several windows.
        bool wasSelected = ReferenceEquals(_sel, t);
        var order = Selectables();
        int index = order.IndexOf(t);

        if (t.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(t.Thumb);
        t.Thumb = IntPtr.Zero;
        _stage.Children.Remove(t.Ring);
        _stage.Children.Remove(t.Label);
        _tiles.Remove(t);
        if (wasSelected) _sel = null;
        if (_state != State.Open) return;
        Relayout();
        StartAnim(FilterMs, 1, null);

        if (wasSelected)
        {
            order.Remove(t);
            if (order.Count > 0 && index >= 0) Select(order[Math.Min(index, order.Count - 1)]);
        }
    }

    // ------------------------------------------------------------------ apps row

    void ClearApps()
    {
        foreach (var a in _apps) _stage.Children.Remove(a.Root);
        if (_sel is AppTile sa && _apps.Contains(sa)) _sel = null;
        _apps.Clear();
    }

    void ClearFiles()
    {
        _fileTimer.Stop();
        foreach (var f in _files) _stage.Children.Remove(f.Root);
        if (_sel is AppTile sf && _files.Contains(sf)) _sel = null;
        _files.Clear();
    }

    // ------------------------------------------------------------------ app grid

    void UpdateGridButton() =>
        _gridButton.Background = _gridMode ? GridButtonOn : _gridButton.IsMouseOver ? GridButtonHover : Brushes.Transparent;

    /// <summary>All installed apps, A–Z. Built once and reused until the app list changes.</summary>
    void EnsureGridBuilt()
    {
        var source = AppCatalog.Apps;
        if (ReferenceEquals(source, _gridSource) && _gridApps.Count > 0) return;
        _gridPanel.Children.Clear();
        _gridApps.Clear();
        _gridSource = source;
        foreach (var app in source.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var tile = new AppTile(app, 0) { InGrid = true };
            CreateAppVisuals(tile, _gridPanel);
            tile.Root.Margin = new Thickness(4);
            _gridApps.Add(tile);
        }
    }

    void ShowGrid()
    {
        if (_state != State.Open || _gridMode) return;
        if (Searching)
        {
            _suppressText = true;
            _search.Text = "";
            _suppressText = false;
            ClearApps();
            ClearFiles();
            foreach (var t in _tiles) t.Visible = true;
        }
        ClearChips();
        Select(null);
        _gridMode = true;
        EnsureGridBuilt();

        double usable = _mon.Width / _dpi - 2 * SideMargin;
        int cols = (int)Math.Clamp(Math.Floor(usable / (AppTileW + 8)), 3, 8);
        _gridPanel.Width = cols * (AppTileW + 8);
        _gridHost.Margin = new Thickness(SideMargin + _taskbarInset.Left / _dpi, AppsTop,
                                         SideMargin + _taskbarInset.Right / _dpi, BottomMargin + _taskbarInset.Bottom / _dpi);
        _gridHost.ScrollToTop();
        _gridHost.Visibility = Visibility.Visible;

        _empty.Text = "Loading apps…";
        _empty.Visibility = _gridApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateGridButton();
        LockHover();
        Relayout();
        StartAnim(FilterMs, 1, null);
        _search.Focus();
    }

    void HideGrid()
    {
        if (!_gridMode) return;
        LeaveGrid();
        LockHover();
        Relayout();
        StartAnim(FilterMs, 1, null);
        if (!Searching) SelectDefault();
        _search.Focus();
    }

    /// <summary>Leaves grid mode without starting an animation (the caller does that).</summary>
    void LeaveGrid()
    {
        if (_sel is AppTile { InGrid: true }) Select(null);
        _gridMode = false;
        _gridHost.Visibility = Visibility.Collapsed;
        _empty.Visibility = Visibility.Collapsed;
        UpdateGridButton();
    }

    // ------------------------------------------------------------------ web suggestions

    void ClearChips()
    {
        _suggestTimer.Stop();
        _suggestCts?.Cancel();
        _suggestCts = null;
        if (_sel is Chip) Select(null);
        foreach (var c in _chips) _stage.Children.Remove(c.Root);
        _chips.Clear();
    }

    /// <summary>Called on every keystroke; waits a moment for typing to pause before asking the engine.</summary>
    void QueueSuggestions()
    {
        _suggestTimer.Stop();
        if (!SuggestionsOn || !WebScope || WebText.Length < 2 || WebSearch.AsUrl(WebText) != null) { ClearChips(); return; }
        _suggestTimer.Start();
    }

    async void FetchSuggestions()
    {
        string query = WebText;
        _suggestCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _suggestCts = cts;
        List<string> list;
        try { list = await WebSuggest.FetchAsync(query, MaxChips, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { Log.Write("suggestions: " + e.Message); return; }

        // Ignore answers that arrive after the user typed more, closed the overview, or opened the grid.
        if (cts.IsCancellationRequested || _state != State.Open || _gridMode || WebText != query) return;
        ShowChips(list);
    }

    void ShowChips(List<string> texts)
    {
        int selectedIndex = _sel is Chip sc ? _chips.IndexOf(sc) : -1;
        if (_sel is Chip) Select(null);
        foreach (var c in _chips) _stage.Children.Remove(c.Root);
        _chips.Clear();

        foreach (var text in texts)
        {
            var chip = new Chip(text);
            var glyph = new TextBlock
            {
                Text = "",
                FontFamily = IconFont,
                FontSize = 11,
                Foreground = DimTextBrush,
                VerticalAlignment = VerticalAlignment.Top, // stays on the first line when the text wraps
                Margin = new Thickness(0, 4, 9, 0)
            };
            var label = new TextBlock
            {
                Text = text,
                Foreground = TextBrush,
                FontSize = 13,
                LineHeight = 18,
                TextWrapping = TextWrapping.Wrap, // long suggestions wrap instead of being cut off
                VerticalAlignment = VerticalAlignment.Center
            };
            var dock = new DockPanel();
            DockPanel.SetDock(glyph, Dock.Left);
            dock.Children.Add(glyph);
            dock.Children.Add(label);
            chip.Root = new Border
            {
                MinHeight = ChipRowH,
                MaxWidth = ChipMaxW,   // as wide as the text, up to the search bar's width
                CornerRadius = new CornerRadius(ChipRowH / 2),
                Padding = new Thickness(14, 7, 16, 7),
                Background = LabelBrush,
                BorderBrush = Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Child = dock
            };
            chip.Root.MouseEnter += (_, _) => { if (!_hoverLocked) Select(chip); };
            chip.Root.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!OpenKeyboardSelection()) SearchChip(chip); };
            chip.Root.Measure(new Size(ChipMaxW, double.PositiveInfinity));
            _stage.Children.Add(chip.Root);
            _chips.Add(chip);
        }
        double reservedBefore = ChipReserve();
        PositionChips();
        if (selectedIndex >= 0 && _chips.Count > 0) Select(_chips[Math.Min(selectedIndex, _chips.Count - 1)]);

        // Normally the list fits the space kept free for it; if long suggestions wrapped past that,
        // make room by sliding the windows down.
        if (ChipReserve() != reservedBefore)
        {
            Relayout();
            StartAnim(FilterMs, 1, null);
        }
    }

    /// <summary>
    /// Stacks the suggestions top to bottom, each centred horizontally.
    /// Suggestions that would push into the lower half of the screen are dropped.
    /// </summary>
    void PositionChips()
    {
        double screenW = _mon.Width / _dpi, screenH = _mon.Height / _dpi;
        double top = ResultsBottom + 12;
        double y = top;
        int fit = 0;
        foreach (var c in _chips)
        {
            double w = c.Root.DesiredSize.Width, h = c.Root.DesiredSize.Height;
            if (fit > 0 && y + h > screenH * 0.55) break;
            double x = (screenW - w) / 2; // each suggestion centred horizontally
            c.Bounds = new Rect(x, y, w, h);
            Canvas.SetLeft(c.Root, x);
            Canvas.SetTop(c.Root, y);
            y += h + ChipGap;
            fit++;
        }
        while (_chips.Count > fit)
        {
            _stage.Children.Remove(_chips[^1].Root);
            _chips.RemoveAt(_chips.Count - 1);
        }
        _chipsHeight = fit > 0 ? y - ChipGap - top : 0;
    }

    double _chipsHeight;

    const double FilesTop = AppsTop + AppTileH + 4;

    /// <summary>Where the result rows end (DIP): under the files row when there is one.</summary>
    double ResultsBottom => _files.Count > 0 ? FilesTop + AppTileH : AppsTop + AppTileH;

    /// <summary>Height kept free for the suggestion list while searching: room for the usual
    /// one-line suggestions, or more if they wrapped. Keeps windows still as suggestions update.</summary>
    double ChipReserve()
    {
        if (!SuggestionsOn || !Searching || _gridMode) return 0;
        double standard = MaxChips * ChipRowH + (MaxChips - 1) * ChipGap;
        return Math.Max(standard, _chipsHeight) + 12;
    }

    void SearchChip(Chip chip)
    {
        if (_state != State.Open) return;
        string text = chip.Text;
        Launcher.Run(() => WebSearch.Run(text));
        Dismiss(null);
    }

    // ------------------------------------------------------------------ right-click menus

    ContextMenu? _openMenu;

    MenuItem MenuEntry(string header, string glyph, Action click)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14, Foreground = TextBrush }
        };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Runs something that opens another app or window, then closes the overview.</summary>
    void RunAndClose(Action action)
    {
        if (_state != State.Open) return;
        Launcher.Run(action); // never block the overview (UAC prompts, slow app starts)
        Dismiss(null);
    }

    void OpenMenu(ContextMenu menu, FrameworkElement target, bool atMouse)
    {
        MenuStyles.Install(); // normally already done by WarmUp
        CloseMenu();
        menu.PlacementTarget = target;
        menu.Placement = atMouse ? System.Windows.Controls.Primitives.PlacementMode.MousePoint
                                 : System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMenu, menu)) _openMenu = null;
            if (_state == State.Open) _search.Focus();
        };
        _openMenu = menu;
        menu.IsOpen = true;
    }

    void CloseMenu()
    {
        if (_openMenu != null) _openMenu.IsOpen = false;
        _openMenu = null;
    }

    void ShowAppMenu(AppTile a, bool atMouse)
    {
        MenuStyles.Install(); // before the menu is built, so its style applies
        if (_state != State.Open || a.IsWeb) return;
        LockHover();
        Select(a);
        var app = a.App;
        if (app.Kind != EntryKind.App) { ShowResultMenu(a, atMouse); return; }
        string? file = ShellActions.ResolveFile(app);
        bool store = ShellActions.IsStoreApp(app);

        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Open", "", () => LaunchApp(a)));
        if (!store)
            menu.Items.Add(MenuEntry("Run as administrator", "",
                () => RunAndClose(() => ShellActions.RunAsAdmin(file ?? AppCatalog.ParsingName(app)!))));
        if (ShellActions.CanOpenLocation(file))
            menu.Items.Add(MenuEntry("Open file location", "", () => RunAndClose(() => ShellActions.OpenFileLocation(file!))));
        menu.Items.Add(new Separator());
        if (file != null)
            menu.Items.Add(MenuEntry("Properties", "", () => RunAndClose(() => ShellActions.ShowProperties(file))));
        menu.Items.Add(MenuEntry("Uninstall…", "", () => RunAndClose(ShellActions.OpenInstalledApps)));
        OpenMenu(menu, a.Root, atMouse);
    }

    /// <summary>Right-click menu for files, folders, commands, settings pages and Control Panel items.</summary>
    void ShowResultMenu(AppTile a, bool atMouse)
    {
        var e = a.App;
        string? path = e.Kind is EntryKind.File or EntryKind.Folder or EntryKind.Command ? e.Target : null;
        bool exists = path != null && !path.StartsWith(@"\\") && (File.Exists(path) || Directory.Exists(path));
        string ext = path != null ? Path.GetExtension(path).ToLowerInvariant() : "";

        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Open", "\uE8A7", () => LaunchApp(a)));
        if (exists && ext is ".exe" or ".msc" or ".cpl" or ".bat" or ".cmd" or ".ps1")
            menu.Items.Add(MenuEntry("Run as administrator", "\uE7EF", () => RunAndClose(() => ShellActions.RunAsAdmin(path!))));
        if (exists && path!.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0)
            menu.Items.Add(MenuEntry(e.Kind == EntryKind.Folder ? "Show in parent folder" : "Open file location", "\uE838",
                () => RunAndClose(() => ShellActions.OpenFileLocation(path!))));
        if (path != null)
            menu.Items.Add(MenuEntry("Copy path", "\uE8C8", () =>
            {
                try { Clipboard.SetText(path); } catch (Exception ex) { Log.Write(ex); }
                Dismiss(null);
            }));
        if (exists)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry("Properties", "\uE946", () => RunAndClose(() => ShellActions.ShowProperties(path!))));
        }
        OpenMenu(menu, a.Root, atMouse);
    }

    void ShowWindowMenu(Tile t, bool atMouse)
    {
        MenuStyles.Install(); // before the menu is built, so its style applies
        if (_state != State.Open) return;
        LockHover();
        Select(t);
        var w = t.Win;
        bool store = ShellActions.IsStoreAumid(w.Aumid);
        string? launchTarget = store ? @"shell:AppsFolder\" + w.Aumid : w.ExePath;

        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Switch to", "", () => ActivateTile(t)));
        if (launchTarget != null)
            menu.Items.Add(MenuEntry("New window", "", () => RunAndClose(() => ShellActions.Launch(launchTarget))));
        if (!store && w.ExePath != null)
            menu.Items.Add(MenuEntry("Run as administrator", "", () => RunAndClose(() => ShellActions.RunAsAdmin(w.ExePath))));
        if (!store && ShellActions.CanOpenLocation(w.ExePath))
            menu.Items.Add(MenuEntry("Open file location", "", () => RunAndClose(() => ShellActions.OpenFileLocation(w.ExePath!))));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("Close", "", () => CloseWindow(t)));
        menu.Items.Add(MenuEntry("End task", "", () => ForceCloseWindow(t)));
        OpenMenu(menu, t.Ring, atMouse);
    }

    void EnsureVisible(object? o)
    {
        if (o is AppTile { InGrid: true } g) g.Root.BringIntoView();
    }

    void CreateAppVisuals(AppTile a, Panel parent)
    {
        // The icon loads in the background the first time; a faint glyph shows until it arrives.
        string? iconName = a.IsWeb ? WebSearch.BrowserExe() : AppCatalog.ParsingName(a.App);
        var glyph = new TextBlock
        {
            Text = a.IsWeb ? "\uE774" : a.App.Kind switch // globe / kind of result
            {
                EntryKind.Setting or EntryKind.ControlPanel => "\uE713",  // gear
                EntryKind.Folder => "\uE8B7",                             // folder
                EntryKind.File => "\uE8A5",                               // document
                EntryKind.Command => "\uE756",                            // command prompt
                EntryKind.Power => SearchSources.PowerGlyph(a.App),
                EntryKind.Calc => "\uE8EF",                               // calculator
                _ => "\uE71D"                                             // generic app
            },
            FontFamily = IconFont,
            FontSize = 36,
            Foreground = TextBrush,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.5
        };
        var image = new Image();
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var iconBox = new Grid();
        iconBox.Children.Add(glyph);
        iconBox.Children.Add(image);
        if (iconName != null)
            IconLoader.GetAsync(iconName, 96, icon =>
            {
                image.Source = icon;
                glyph.Visibility = icon != null ? Visibility.Collapsed : Visibility.Visible;
                glyph.Opacity = 1;
            });
        else glyph.Opacity = 1;
        FrameworkElement img = iconBox;
        img.Width = 52;
        img.Height = 52;
        img.Margin = new Thickness(0, 12, 0, 7);
        img.HorizontalAlignment = HorizontalAlignment.Center;

        string label = !a.IsWeb ? a.App.Name
                     : WebSearch.AsUrl(WebText) is string url ? $"Open {url.Replace("https://", "")}"
                     : $"Search the web for “{WebText}”";
        var name = new TextBlock
        {
            Text = label,
            Foreground = TextBrush,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 34,
            Margin = new Thickness(6, 0, 6, 0)
        };
        var sp = new StackPanel();
        sp.Children.Add(img);
        sp.Children.Add(name);
        a.Root = new Border
        {
            Width = AppTileW,
            Height = AppTileH,
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Child = sp,
            ToolTip = a.IsWeb ? "Search in your default browser" : a.App.Detail ?? a.App.Name
        };
        a.Root.MouseEnter += (_, _) => { if (!_hoverLocked) Select(a); };
        a.Root.MouseLeftButtonUp += (_, e) => { e.Handled = true; if (!OpenKeyboardSelection()) LaunchApp(a); };
        a.Root.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowAppMenu(a, atMouse: true); };
        parent.Children.Add(a.Root);
    }

    // ------------------------------------------------------------------ layout & animation

    void Relayout()
    {
        double d = _dpi;
        double W = _mon.Width, H = _mon.Height;

        // App results row, centred under the search bar.
        double rowW = _apps.Count * AppTileW + Math.Max(0, _apps.Count - 1) * AppGap;
        double x = (W / d - rowW) / 2;
        foreach (var a in _apps)
        {
            a.Bounds = new Rect(x, AppsTop, AppTileW, AppTileH);
            Canvas.SetLeft(a.Root, x);
            Canvas.SetTop(a.Root, AppsTop);
            x += AppTileW + AppGap;
        }

        // Files and folders, a second row under it.
        double filesW = _files.Count * AppTileW + Math.Max(0, _files.Count - 1) * AppGap;
        double fx = (W / d - filesW) / 2;
        foreach (var f in _files)
        {
            f.Bounds = new Rect(fx, FilesTop, AppTileW, AppTileH);
            Canvas.SetLeft(f.Root, fx);
            Canvas.SetTop(f.Root, FilesTop);
            fx += AppTileW + AppGap;
        }

        double topDip = _apps.Count > 0 ? ResultsBottom + 24 : AppsTop + 6;
        // Keep the suggestion row reserved while searching, so windows don't jump when suggestions arrive.
        topDip += ChipReserve();
        var inset = _taskbarInset;
        var area = new Rect(SideMargin * d + inset.Left, topDip * d,
                            Math.Max(1, W - 2 * SideMargin * d - inset.Left - inset.Right),
                            Math.Max(1, H - (topDip + BottomMargin) * d - inset.Bottom));

        // In the app grid every window fades out of the way.
        var visible = _gridMode ? new List<Tile>() : _tiles.Where(t => t.Visible).ToList();
        var rects = Layout.Compute(visible.Select(t => t.LayoutSize).ToList(), area, Spacing * d, LabelSpace * d, 1.0);
        for (int i = 0; i < visible.Count; i++)
        {
            visible[i].Target = rects[i];
            visible[i].TargetOpacity = 1;
        }
        foreach (var t in _tiles.Except(visible))
        {
            t.Target = Shrink(t.Current, 0.85);
            t.TargetOpacity = 0;
        }
        foreach (var t in _tiles) MeasureLabel(t);

        if (_gridMode) return; // the grid manages its own "loading" message
        _empty.Text = "No results";
        _empty.Visibility = Searching && visible.Count == 0 && _apps.Count == 0 && _files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void MeasureLabel(Tile t)
    {
        t.Label.MaxWidth = Math.Max(150, t.Target.Width / _dpi);
        t.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }

    static Rect Shrink(Rect r, double f)
    {
        if (r.IsEmpty) return new Rect(0, 0, 0, 0);
        double w = r.Width * f, h = r.Height * f;
        return new Rect(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
    }

    static double Lerp(double a, double b, double k) => a + (b - a) * k;

    static Rect Lerp(Rect a, Rect b, double k) =>
        new(Lerp(a.X, b.X, k), Lerp(a.Y, b.Y, k), Math.Max(0, Lerp(a.Width, b.Width, k)), Math.Max(0, Lerp(a.Height, b.Height, k)));

    void StartAnim(double ms, double chromeTo, Action? done)
    {
        foreach (var t in _tiles)
        {
            t.From = t.Current;
            t.FromOpacity = t.Opacity;
        }
        _chromeFrom = _chrome;
        _chromeTo = chromeTo;
        double speed = App.Settings.AnimationSpeed; // 0 = off, 0.5 fast, 1 normal, 1.6 slow
        _animMs = SystemParameters.ClientAreaAnimation && speed > 0 ? ms * speed : 0;
        _animStart = Stopwatch.GetTimestamp();
        _animDone = done;
        if (!_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
        Step();
    }

    void StopRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    void OnRendering(object? sender, EventArgs e) => Step();

    void Step()
    {
        double t = _animMs <= 0 ? 1 : Math.Min(1, Stopwatch.GetElapsedTime(_animStart).TotalMilliseconds / _animMs);
        double k = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        _chrome = Lerp(_chromeFrom, _chromeTo, k);
        ApplyChrome();
        foreach (var tile in _tiles)
        {
            tile.Current = Lerp(tile.From, tile.Target, k);
            tile.Opacity = Lerp(tile.FromOpacity, tile.TargetOpacity, k);
            ApplyTile(tile);
        }
        if (t >= 1)
        {
            StopRendering();
            var done = _animDone;
            _animDone = null;
            done?.Invoke();
        }
    }

    void ApplyChrome()
    {
        // The blur fades in over the sharp wallpaper (and darkens) only if the wallpaper could be seen;
        // if windows covered the screen, it's blurred from the start.
        double background = _fadeBackground ? _chrome : 1;
        _bg.Opacity = background;
        _dim.Opacity = background;
        _stage.Opacity = _chrome;
        _gridHost.Opacity = _chrome;
        _searchBorder.Opacity = _chrome;
    }

    void ApplyTile(Tile t)
    {
        var r = t.Current;
        byte op = (byte)Math.Clamp(Math.Round(t.Opacity * 255), 0, 255);

        if (t.Thumb != IntPtr.Zero)
        {
            var p = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY |
                          Native.DWM_TNP_SOURCECLIENTAREAONLY | (t.HasCrop ? Native.DWM_TNP_RECTSOURCE : 0),
                rcDestination = new Native.RECT((int)Math.Round(r.Left), (int)Math.Round(r.Top),
                                                (int)Math.Round(r.Right), (int)Math.Round(r.Bottom)),
                rcSource = t.Crop,
                opacity = op,
                fVisible = op > 0 && r.Width >= 2 && r.Height >= 2,
                fSourceClientAreaOnly = false
            };
            Native.DwmUpdateThumbnailProperties(t.Thumb, ref p);
        }

        double d = _dpi;
        double x = r.X / d, y = r.Y / d, w = r.Width / d, h = r.Height / d;
        Canvas.SetLeft(t.Ring, x - RingPad);
        Canvas.SetTop(t.Ring, y - RingPad);
        t.Ring.Width = w + RingPad * 2;
        t.Ring.Height = h + RingPad * 2;
        t.Ring.Opacity = t.Opacity;

        Canvas.SetLeft(t.Label, x + w / 2 - t.Label.DesiredSize.Width / 2);
        Canvas.SetTop(t.Label, y + h + 10);
        t.Label.Opacity = t.Opacity;

        bool hit = t.Visible && t.Opacity > 0.3;
        t.Ring.IsHitTestVisible = hit;
        t.Label.IsHitTestVisible = hit;
    }

    // ------------------------------------------------------------------ search

    void OnQueryChanged()
    {
        if (_state != State.Open) return;
        LockHover();
        if (_gridMode && Searching) LeaveGrid(); // typing in the app grid switches to search results

        // Ranking = how well the text matches + what you've used (Usage): what you picked for
        // these letters before (most recent first), and how often/recently you use the app.
        string query = _search.Text;
        var (scope, text) = SearchSources.ParseScope(query);
        bool all = scope == SearchSources.Scope.All;
        var tokens = Search.Tokenize(text);
        Tile? bestTile = null;
        double bestTileMatch = 0;
        foreach (var t in _tiles)
        {
            if (tokens.Length == 0 && all) { t.Visible = true; t.Score = 0; continue; }
            if (!all) { t.Visible = false; t.Score = 0; continue; } // a filter ("photos:") hides the windows
            double match = Math.Max(Search.Score(tokens, t.Win.Title),
                           Math.Max(Search.Score(tokens, t.Win.AppName),
                                    Search.Score(tokens, t.Win.ProcName) * 0.9));
            t.Visible = match > 0;
            t.Score = t.Visible ? match + Usage.Bonus(t.Key, query) : 0;
            if (t.Visible && (bestTile == null || t.Score > bestTile.Score)) bestTile = t;
            if (t.Visible) bestTileMatch = Math.Max(bestTileMatch, match);
        }

        ClearApps();
        double bestAppMatch = 0;
        AppEntry? calc = all ? SearchSources.Calculate(text) : null;
        if (calc != null)
        {
            // Arithmetic: the answer comes first, and Enter copies it.
            var tile = new AppTile(calc, 1000);
            CreateAppVisuals(tile, _stage);
            _apps.Add(tile);
        }
        if (tokens.Length > 0 && scope is SearchSources.Scope.All or SearchSources.Scope.Apps or SearchSources.Scope.Settings)
        {
            // Apps, then what else Start's search finds: Windows Tools, places and drives, Settings pages,
            // Control Panel, power actions, and Run-dialog commands and paths ("regedit", "%appdata%").
            IEnumerable<AppEntry> candidates = scope switch
            {
                SearchSources.Scope.Apps => AppCatalog.Apps.Concat(AppCatalog.Extras.Where(e => e.Kind == EntryKind.App)),
                SearchSources.Scope.Settings => SearchSources.SettingsPages.Concat(AppCatalog.Extras.Where(e => e.Kind == EntryKind.ControlPanel)),
                _ => AppCatalog.Apps.Concat(AppCatalog.Extras).Concat(SearchSources.SettingsPages).Concat(SearchSources.PowerActions)
            };
            var command = all ? SearchSources.MatchCommand(text) : null;
            if (command != null)
            {
                // Typing an app's exe name finds the app itself already ("regedit" → Registry Editor).
                string stem = command.ExeStem ?? "";
                bool known = stem.Length > 0 && AppCatalog.Apps.Concat(AppCatalog.Extras).Any(a => a.ExeStem == stem);
                if (!known) candidates = candidates.Append(command);
            }
            var matches = candidates
                // A typed path is surely meant; a program name ("winver") ranks just under a strong app match.
                .Select(a => (a, m: a == command ? (command.Kind == EntryKind.Command && command.ExeStem != null ? 88 : 97)
                                                 : SearchSources.Match(tokens, a)))
                .Where(x => x.m > 0)
                .Select(x => (x.a, x.m, s: x.m + Usage.Bonus(x.a.Id, query)))
                .OrderByDescending(x => x.s)
                .ThenBy(x => x.a.Name.Length)
                .Take(Math.Min(MaxAppResults, AppTilesThatFit()) - (all ? 1 : 0) - (calc != null ? 1 : 0)) // room for web/answer
                .ToList();
            foreach (var (a, m, s) in matches)
            {
                var tile = new AppTile(a, s);
                CreateAppVisuals(tile, _stage);
                _apps.Add(tile);
                bestAppMatch = Math.Max(bestAppMatch, m);
            }
        }
        if (tokens.Length > 0 && WebScope)
        {
            // Always offer a web search as the last result.
            var web = new AppTile(WebEntry, 0) { IsWeb = true };
            CreateAppVisuals(web, _stage);
            _apps.Add(web);
        }

        FilterFiles(tokens);
        if (_chips.Count > 0) PositionChips(); // the files row may have come or gone
        Relayout();
        StartAnim(FilterMs, 1, null);
        QueueSuggestions();
        QueueFiles();

        // Default selection (what Enter does): the best window or app match, unless nothing
        // matches well, e.g. "how many days until christmas", then the web search.
        // No app matched well: it may have been installed just now, so read the list again (the results
        // update by themselves if it turns up).
        if (tokens.Length > 0 && scope is SearchSources.Scope.All or SearchSources.Scope.Apps && bestAppMatch < 80)
            AppCatalog.RefreshSoon();
        if (tokens.Length == 0 && all) { SelectDefault(); return; } // search cleared: back to the previous window
        if (calc != null) { Select(_apps[0]); _defaultSel = _sel; return; }
        if (scope == SearchSources.Scope.Web && WebSearch.AsUrl(text) == null && _apps.Count > 0) { Select(_apps[^1]); _defaultSel = _sel; return; }
        var bestApp = _apps.FirstOrDefault(a => !a.IsWeb); // already sorted best-first
        double appScore = bestApp?.Score ?? 0, winScore = bestTile?.Score ?? 0;
        bool keepFile = _sel is AppTile { IsFile: true } && !ReferenceEquals(_sel, _defaultSel); // picked by you
        // "Nothing matches well" is judged on the text match alone, not on usage.
        if (keepFile) { }
        else if (Math.Max(bestAppMatch, bestTileMatch) < WebThreshold)
        {
            var file = _files.FirstOrDefault();
            var webTile = _apps.FirstOrDefault(a => a.IsWeb);
            if (file != null && (file.Score >= WebThreshold || webTile == null)) Select(file);
            else Select(webTile ?? bestApp);
        }
        else if (bestTile != null && winScore >= appScore) Select(bestTile);
        else if (bestApp != null && OpenWindowOf(bestApp) is Tile open) Select(open); // already open: switch, don't start another
        else Select(bestApp);
        if (!keepFile) _defaultSel = _sel;
    }

    // ------------------------------------------------------------------ file results

    const int MaxFileResults = 8;
    bool FilesOn => App.Settings.SearchFiles && !_gridMode;

    /// <summary>Whether this search looks for files: two letters or more, or a file filter ("photos:") alone.</summary>
    bool WantsFiles()
    {
        if (!FilesOn) return false;
        var (scope, text) = Scoped;
        if (SearchSources.IsFileScope(scope)) return true;
        return scope == SearchSources.Scope.All && text.Trim().Length >= 2 && SearchSources.Calculate(text) == null;
    }

    /// <summary>Drops file results that no longer match what's typed, at once (new ones follow shortly).</summary>
    void FilterFiles(string[] tokens)
    {
        if (!WantsFiles()) { ClearFiles(); return; }
        foreach (var f in _files.ToList())
        {
            if (tokens.Length == 0 || Search.Score(tokens, f.App.Name) > 0 || f.Score < WebThreshold) continue;
            _stage.Children.Remove(f.Root);
            if (ReferenceEquals(_sel, f)) _sel = null;
            _files.Remove(f);
        }
    }

    void QueueFiles()
    {
        _fileTimer.Stop();
        if (WantsFiles()) _fileTimer.Start();
    }

    void FetchFiles()
    {
        if (_state != State.Open || !WantsFiles()) return;
        int max = Math.Min(MaxFileResults, AppTilesThatFit());
        SearchSources.FindFiles(_search.Text, max, ShowFiles);
    }

    void ShowFiles(string query, List<SearchSources.FileHit> hits)
    {
        // Ignore answers to text that's no longer in the box, or that arrive after closing.
        if (_state != State.Open || !WantsFiles() || _search.Text != query) return;
        int max = Math.Min(MaxFileResults, AppTilesThatFit());
        int selIndex = _sel is AppTile st && _files.Contains(st) ? _files.IndexOf(st) : -1;
        bool wasDefault = ReferenceEquals(_sel, _defaultSel);
        ClearFiles();
        // Places already in the row above (Downloads, a drive) aren't shown twice.
        var above = new HashSet<string>(_apps.Where(x => x.App.Target != null).Select(x => x.App.Target!.TrimEnd('\\')),
                                         StringComparer.OrdinalIgnoreCase);
        var ranked = hits
            .Where(h => !above.Contains(h.Entry.Target!.TrimEnd('\\')))
            .Select(h => (h.Entry, s: h.Score + Usage.Bonus(h.Entry.Id, query)))
            .OrderByDescending(x => x.s)
            .Take(max);
        foreach (var (entry, score) in ranked)
        {
            var tile = new AppTile(entry, score) { IsFile = true };
            CreateAppVisuals(tile, _stage);
            _files.Add(tile);
        }
        PositionChips();
        Relayout();
        StartAnim(FilterMs, 1, null);

        if (selIndex >= 0 && _files.Count > 0) Select(_files[Math.Min(selIndex, _files.Count - 1)]);
        else if (wasDefault || _sel == null)
        {
            // Nothing else matched well, but a file does (or only files are wanted): make it what Enter opens.
            bool onlyFiles = SearchSources.IsFileScope(Scoped.Scope);
            bool webDefault = _sel is AppTile { IsWeb: true } || _sel == null;
            var best = _files.FirstOrDefault();
            if (webDefault && best != null && (onlyFiles || best.Score >= WebThreshold)) { Select(best); _defaultSel = best; }
        }
    }

    /// <summary>How many result tiles fit side by side on this screen.</summary>
    int AppTilesThatFit() =>
        Math.Max(2, (int)((_mon.Width / _dpi - 2 * SideMargin + AppGap) / (AppTileW + AppGap)));

    /// <summary>A visible window of the app this result starts, if one is open.</summary>
    Tile? OpenWindowOf(AppTile a) =>
        _tiles.Where(t => t.Visible && t.Key != null && string.Equals(t.Key, a.App.Id, StringComparison.OrdinalIgnoreCase))
              .OrderByDescending(t => t.Mru)
              .FirstOrDefault();

    // ------------------------------------------------------------------ selection & input

    /// <summary>
    /// Tab / scroll order. Without a search: windows from most to least recently used, like Alt+Tab.
    /// With a search: app results, then windows from best to worst match.
    /// </summary>
    List<object> Selectables()
    {
        if (_gridMode) return new List<object>(_gridApps);
        var list = new List<object>(_apps);
        list.AddRange(_files);
        list.AddRange(_chips);
        // _tiles is in the same order the layout fills its rows, so this is reading order:
        // left to right along each row, then the next row down.
        list.AddRange(_tiles.Where(t => t.Visible));
        return list;
    }

    /// <summary>Visible windows from most to least recently used (for the default selection).</summary>
    List<Tile> RecentWindows() =>
        _tiles.Where(t => t.Visible).OrderByDescending(t => t.Mru).ThenBy(t => t.ZIndex).ToList();

    bool Searching => _search.Text.Trim().Length > 0;

    /// <summary>The search with a filter word split off: "photos: beach" → (Photos, "beach").</summary>
    (SearchSources.Scope Scope, string Text) Scoped => SearchSources.ParseScope(_search.Text);

    /// <summary>The text that goes to the web: what's typed, minus a filter word.</summary>
    string WebText => Scoped.Text.Trim();

    bool WebScope => Scoped.Scope is SearchSources.Scope.All or SearchSources.Scope.Web;

    Rect BoundsOf(object o) => o switch
    {
        Tile t => new Rect(t.Target.X / _dpi, t.Target.Y / _dpi, t.Target.Width / _dpi, t.Target.Height / _dpi),
        AppTile { InGrid: true } g => GridBounds(g),
        AppTile a => a.Bounds,
        Chip c => c.Bounds,
        _ => Rect.Empty
    };

    /// <summary>Where a grid tile sits in its wrapped, scrolled grid. Only the layout position
    /// matters for arrow-key navigation, so tiles scrolled out of view work too.</summary>
    Rect GridBounds(AppTile g)
    {
        try { return g.Root.TransformToAncestor(_gridPanel).TransformBounds(new Rect(0, 0, g.Root.ActualWidth, g.Root.ActualHeight)); }
        catch { return Rect.Empty; }
    }

    void Select(object? o)
    {
        _wheelSelected = false;
        if (ReferenceEquals(o, _sel)) return;
        SetSelected(_sel, false);
        _sel = o;
        SetSelected(_sel, true);
    }

    void SetSelected(object? o, bool on)
    {
        switch (o)
        {
            case Tile t:
                t.Ring.BorderBrush = on ? RingBrush : Brushes.Transparent;
                t.Label.Background = on ? LabelSelBrush : LabelBrush;
                t.CloseButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                MeasureLabel(t);
                ApplyTile(t);
                break;
            case AppTile a:
                a.Root.Background = on ? AppSelBrush : Brushes.Transparent;
                break;
            case Chip c:
                c.Root.Background = on ? LabelSelBrush : LabelBrush;
                c.Root.BorderBrush = on ? RingBrush : Frozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
                break;
        }
    }

    void Navigate(int dx, int dy)
    {
        var items = Selectables();
        if (items.Count == 0) return;
        if (_sel == null || !items.Contains(_sel)) { Select(items[0]); EnsureVisible(_sel); return; }

        var cur = BoundsOf(_sel);
        var c = new Point(cur.X + cur.Width / 2, cur.Y + cur.Height / 2);
        object? best = null;
        double bestCost = double.MaxValue;
        foreach (var o in items)
        {
            if (ReferenceEquals(o, _sel)) continue;
            var b = BoundsOf(o);
            double vx = b.X + b.Width / 2 - c.X, vy = b.Y + b.Height / 2 - c.Y;
            double primary = dx != 0 ? vx * dx : vy * dy;
            double perp = dx != 0 ? Math.Abs(vy) : Math.Abs(vx);
            if (primary <= 1) continue;
            double cost = primary + perp * 2.5;
            if (cost < bestCost) { bestCost = cost; best = o; }
        }
        if (best != null) Select(best);
        EnsureVisible(_sel);
    }

    void Cycle(int dir)
    {
        var items = Selectables();
        if (items.Count == 0) return;
        // Reading order, wrapping around at the ends.
        int i = _sel == null ? -1 : items.IndexOf(_sel);
        if (i < 0) i = dir > 0 ? 0 : items.Count - 1;
        else i = (i + dir + items.Count) % items.Count;
        Select(items[i]);
        EnsureVisible(_sel);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_state != State.Open) return;
        if (_openMenu?.IsOpen == true) return; // the menu handles its own keys
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            // Menu key or Shift+F10: right-click menu for the selected item
            case Key.Apps:
            case Key.F10 when Keyboard.Modifiers == ModifierKeys.Shift:
                e.Handled = true;
                if (_sel is Tile menuTile) ShowWindowMenu(menuTile, atMouse: false);
                else if (_sel is AppTile menuApp) ShowAppMenu(menuApp, atMouse: false);
                break;
            case Key.Escape:
                e.Handled = true;
                if (_search.Text.Length > 0) _search.Text = "";
                else if (_gridMode) HideGrid();
                else Dismiss(null);
                break;
            case Key.Enter:
                e.Handled = true;
                if (_sel is Tile t) ActivateTile(t);
                else if (_sel is AppTile a) LaunchApp(a);
                else if (_sel is Chip c) SearchChip(c);
                else if (_search.Text.Trim().Length == 0) Dismiss(null);
                break;
            // With an empty search box: Backspace closes the selected window normally, Del force-closes it.
            // While typing, both keys edit the search text as usual.
            case Key.Back when _search.Text.Length == 0 && _sel is Tile closeTile:
                e.Handled = true;
                LockHover();
                CloseWindow(closeTile);
                break;
            case Key.Delete when _search.Text.Length == 0 && _sel is Tile killTile:
                e.Handled = true;
                LockHover();
                ForceCloseWindow(killTile);
                break;
            case Key.Left: e.Handled = true; LockHover(); Navigate(-1, 0); break;
            case Key.Right: e.Handled = true; LockHover(); Navigate(1, 0); break;
            case Key.Up: e.Handled = true; LockHover(); Navigate(0, -1); break;
            case Key.Down: e.Handled = true; LockHover(); Navigate(0, 1); break;
            case Key.Tab:
                e.Handled = true;
                LockHover();
                Cycle(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                break;
            default:
                if (!_search.IsKeyboardFocused) Keyboard.Focus(_search);
                break;
        }
    }

    /// <summary>Stops a resting mouse pointer from stealing the keyboard selection until it actually moves.</summary>
    void LockHover()
    {
        _hoverLocked = true;
        // Read the real cursor position from Windows. WPF's Mouse.GetPosition can return a stale
        // position from the last time the mouse was over the overview, which made the first tiny
        // mouse event look like a big move and let the tile under the cursor steal the selection.
        Native.GetCursorPos(out _lockCursor);
    }

    /// <summary>True once the physical cursor has moved clearly away from where it was locked.</summary>
    bool CursorMovedSinceLock()
    {
        if (!Native.GetCursorPos(out var p)) return false;
        double dx = p.X - _lockCursor.X, dy = p.Y - _lockCursor.Y;
        // Generous threshold (in physical pixels): a hand on the scroll wheel nudges the mouse a little.
        return dx * dx + dy * dy >= Math.Pow(24 * _dpi, 2);
    }

    int _wheelAccum;
    bool _wheelSelected; // current selection was made with the scroll wheel

    /// <summary>Scroll wheel steps through windows and apps like Tab / Shift+Tab.</summary>
    void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_state != State.Open) return;
        if (_gridMode) return; // in the app grid the wheel scrolls the grid
        e.Handled = true;
        // Touchpads send many small deltas; one notch of a mouse wheel is 120.
        _wheelAccum += e.Delta;
        while (Math.Abs(_wheelAccum) >= 120)
        {
            int dir = _wheelAccum < 0 ? 1 : -1; // scroll down = next, up = previous
            _wheelAccum += dir * 120;
            LockHover();
            Cycle(dir);
            _wheelSelected = _sel != null;
        }
    }

    /// <summary>
    /// If the selection came from scrolling / the keyboard and the mouse hasn't really moved since,
    /// a click confirms that selection instead of picking whatever is under the pointer.
    /// </summary>
    bool OpenKeyboardSelection()
    {
        if (!_hoverLocked || !_wheelSelected || _sel == null) return false;
        if (_sel is Tile t) ActivateTile(t);
        else if (_sel is AppTile a) LaunchApp(a);
        else if (_sel is Chip c) SearchChip(c);
        return true;
    }

    void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_hoverLocked) return;
        // WPF also sends mouse-move events when tiles slide under a resting cursor;
        // only a real movement of the mouse unlocks hover.
        if (!CursorMovedSinceLock()) return;
        _hoverLocked = false;
        if (_gridMode)
        {
            foreach (var g in _gridApps) if (g.Root.IsMouseOver) { Select(g); return; }
            return;
        }
        foreach (var a in _apps) if (a.Root.IsMouseOver) { Select(a); return; }
        foreach (var f in _files) if (f.Root.IsMouseOver) { Select(f); return; }
        foreach (var c in _chips) if (c.Root.IsMouseOver) { Select(c); return; }
        foreach (var t in _tiles) if (t.Visible && (t.Ring.IsMouseOver || t.Label.IsMouseOver)) { Select(t); return; }
    }

    // ------------------------------------------------------------------ actions

    void ActivateTile(Tile t)
    {
        if (_state != State.Open) return;
        var h = t.Win.Hwnd;
        if (!Native.IsWindow(h)) { RemoveTile(t); return; }
        // Hand focus over while we're still the foreground window (Windows only lets the
        // foreground app do that), then animate out. Losing focus may already start the dismiss.
        _activating = t; // losing focus to this window must not count as "clicked away"
        try
        {
            if (Native.IsIconic(h)) Native.ShowWindowAsync(h, Native.SW_RESTORE);
            if (!Native.SetForegroundWindow(h)) Native.ForceForeground(h);
        }
        finally { _activating = null; }
        // Record the switch right away rather than waiting for Windows' focus event,
        // so an immediate double-tap knows which window is now the "previous" one.
        Mru.Touch(h);
        if (Searching && t.Key != null) Usage.RecordPick(t.Key, _search.Text); // search learns from this too
        Dismiss(t);
    }

    void LaunchApp(AppTile a)
    {
        if (_state != State.Open) return;
        // Started on another thread (Store apps can take seconds), allowed to take focus.
        var app = a.App;
        string query = _search.Text;
        if (app.Kind == EntryKind.Calc)
        {
            try { Clipboard.SetText(app.Target ?? ""); } catch (Exception e) { Log.Write(e); }
            Dismiss(null);
            return;
        }
        if (!a.IsWeb) Usage.RecordPick(app.Id, query); // search learns: these letters → this app
        if (a.IsWeb)
        {
            string text = WebText;
            string? url = WebSearch.AsUrl(text);
            if (url != null) Launcher.Run(() => WebSearch.OpenUrl(url)); // "youtube.com": go there, don't search for it
            else Launcher.Run(() => WebSearch.Run(text));
        }
        else Launcher.Run(() => AppCatalog.Launch(app));
        Dismiss(null);
    }

    /// <summary>
    /// Del: kills the app at once, no "save changes?" prompt. Falls back to a normal close for
    /// File Explorer (it is the taskbar/desktop process) and for apps Windows won't let us kill.
    /// Note: killing an app closes all of its windows.
    /// </summary>
    void ForceCloseWindow(Tile t)
    {
        var w = t.Win;
        string exe = Path.GetFileName(w.ExePath ?? "").ToLowerInvariant();
        if (w.Pid == 0 || w.Pid == (uint)Environment.ProcessId || exe is "explorer.exe" or "applicationframehost.exe" or "")
        {
            CloseWindow(t);
            return;
        }
        try
        {
            using var p = Process.GetProcessById((int)w.Pid);
            p.Kill();
        }
        catch (Exception e)
        {
            Log.Write($"force close {exe} failed ({e.Message}), closing normally");
            CloseWindow(t);
            return;
        }

        // Remove the killed app's tiles as its windows disappear.
        uint pid = w.Pid;
        int ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (_state != State.Open) { timer.Stop(); return; }
            foreach (var gone in _tiles.Where(x => x.Win.Pid == pid && (!Native.IsWindow(x.Win.Hwnd) || !Native.IsWindowVisible(x.Win.Hwnd))).ToList())
                RemoveTile(gone);
            if (ticks >= 30 || !_tiles.Any(x => x.Win.Pid == pid)) timer.Stop();
        };
        timer.Start();
    }

    /// <summary>Backspace / × / middle-click: a normal close, so the app can ask about unsaved work.</summary>
    void CloseWindow(Tile t)
    {
        var h = t.Win.Hwnd;
        Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        int ticks = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (_state != State.Open || !_tiles.Contains(t)) { timer.Stop(); return; }
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsCloaked(h))
            {
                timer.Stop();
                RemoveTile(t);
            }
            else if (ticks >= 8)
            {
                // Still there after ~1s: probably a "save changes?" prompt. Show it.
                timer.Stop();
                ActivateTile(t);
            }
        };
        timer.Start();
    }
}
