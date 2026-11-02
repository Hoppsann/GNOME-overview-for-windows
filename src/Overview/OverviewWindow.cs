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
        public long Mru;               // higher = used more recently
        public int ZIndex;             // z-order position, tie-breaker for Mru
        public Border Ring = null!;
        public Border Label = null!;
        public Border CloseButton = null!;
        public Image Placeholder = null!;
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
    const int MaxAppResults = 8;

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
    readonly Image _bg;
    readonly Canvas _stage;
    readonly Border _searchBorder;
    readonly TextBox _search;
    readonly TextBlock _placeholder;
    readonly TextBlock _empty;

    readonly List<Tile> _tiles = new();
    readonly List<AppTile> _apps = new();

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

        _bg = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(_bg, BitmapScalingMode.Linear);

        var dim = new Border { Background = Frozen(Color.FromArgb(0x5C, 0, 0, 0)) };

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
        _root.Children.Add(_bg);
        _root.Children.Add(dim);
        _root.Children.Add(_stage);
        _root.Children.Add(_gridHost);
        _root.Children.Add(_searchBorder);
        _root.Children.Add(_empty);
        Content = _root;

        _suggestTimer.Tick += (_, _) => { _suggestTimer.Stop(); FetchSuggestions(); };
        MenuStyles.Install();

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

        // Warm up so the first open is as fast as later ones: blur the wallpaper now and
        // run WPF's first layout pass (templates, fonts) while nobody is waiting.
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
            if (_sel is Tile t && !_gridMode)
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
            ShowAutoHideTaskbar(monitor); // with auto-hide on, the work area is the whole screen: make room
            int mw = _mon.Width, mh = _mon.Height;

            var items = WindowEnumerator.Enumerate(_hwnd);

            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _mon.Left, _mon.Top, mw, mh, Native.SWP_NOACTIVATE);
            UpdateBackground();

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
            _empty.Visibility = Visibility.Collapsed;

            Show();
            // WPF may resize the window when it lands on a monitor with a different DPI: pin it again.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, _mon.Left, _mon.Top, mw, mh, 0);
            Native.ForceForeground(_hwnd);
            Activate();
            RaiseTaskbar(); // activating the overview put it above an auto-hide taskbar
            _search.Focus();
            Keyboard.Focus(_search);
            UpdateLayout();
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

            BuildTiles(items);
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

    // ------------------------------------------------------------------ auto-hide taskbar

    IntPtr _autoHideTaskbar;
    int _taskbarEdge;      // 0 left, 1 top, 2 right, 3 bottom
    int _taskbarThickness; // physical pixels
    Thickness _taskbarInset; // physical pixels the window grid stays clear of (auto-hide taskbar on top)
    DispatcherTimer _taskbarTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    /// <summary>
    /// With taskbar auto-hide on, the overview covers the whole screen but keeps the taskbar's strip
    /// free, and keeps the taskbar above itself, including its thin hover edge, so pointing at the
    /// bottom slides it up as usual. Windows 11 gives apps no clean way to show it unasked: nudging
    /// the window is ignored and switching auto-hide off resizes everything, so neither is done.
    /// </summary>
    void ShowAutoHideTaskbar(IntPtr monitor)
    {
        _autoHideTaskbar = IntPtr.Zero;
        _taskbarInset = new Thickness(0);
        _taskbarTimer.Stop();
        if (!Native.IsTaskbarAutoHide()) return;
        var tb = Native.TaskbarOnMonitor(monitor);
        if (tb == IntPtr.Zero || !Native.GetWindowRect(tb, out var r)) return;

        var m = _monFull;
        bool horizontal = r.Width >= r.Height;
        int thickness = horizontal ? r.Height : r.Width;
        if (thickness <= 0 || thickness > (horizontal ? m.Height : m.Width) / 3) return;
        // A hidden taskbar sits just past its screen edge, so its centre tells which edge that is.
        _taskbarEdge = horizontal
            ? ((r.Top + r.Bottom) / 2 > (m.Top + m.Bottom) / 2 ? 3 : 1)
            : ((r.Left + r.Right) / 2 > (m.Left + m.Right) / 2 ? 2 : 0);
        _taskbarThickness = thickness;
        _autoHideTaskbar = tb;

        // Cover the whole screen, including behind the taskbar. The taskbar is see-through, so if the
        // overview stopped short of it, it would show the (maybe bright) app window behind it instead
        // of the overview's background. Windows are kept clear of it with an inset instead.
        _mon = m;
        switch (_taskbarEdge)
        {
            case 0: _taskbarInset.Left = thickness; break;
            case 1: _mon.Top = m.Top + thickness; break; // top: the search bar must stay below it
            case 2: _taskbarInset.Right = thickness; break;
            default: _taskbarInset.Bottom = thickness; break;
        }

        _taskbarTimer.Tick -= OnTaskbarTimer;
        _taskbarTimer.Tick += OnTaskbarTimer;
        _taskbarTimer.Start();
        Log.Write($"auto-hide taskbar: edge {_taskbarEdge}, {thickness}px, kept above the overview");
    }

    /// <summary>Only changes z-order, never the taskbar's position or state, so Explorer's own
    /// show/hide on hover keeps working undisturbed.</summary>
    void OnTaskbarTimer(object? sender, EventArgs e)
    {
        if (_state != State.Open || _autoHideTaskbar == IntPtr.Zero) { _taskbarTimer.Stop(); return; }
        if (!Native.IsAbove(_autoHideTaskbar, _hwnd)) RaiseTaskbar();
    }

    /// <summary>Puts the taskbar back above the overview (activating the overview puts it on top).</summary>
    void RaiseTaskbar()
    {
        if (_autoHideTaskbar == IntPtr.Zero) return;
        Native.SetWindowPos(_autoHideTaskbar, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void FinishHide()
    {
        StopRendering();
        Hide();
        _taskbarTimer.Stop();
        Select(null); // also clears the highlight on a reused app-grid tile
        if (_gridMode) LeaveGrid();
        ClearChips();
        ClearTiles();
        ClearApps();
        _sel = null;
        _state = State.Hidden;
        AppCatalog.RefreshIfStale();
    }

    public void ForceHide()
    {
        try { FinishHide(); } catch (Exception e) { Log.Write(e); }
    }

    // ------------------------------------------------------------------ background

    void UpdateBackground()
    {
        try
        {
            var sb = new StringBuilder(1024);
            Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
            string path = sb.ToString();
            string key = File.Exists(path) ? path + "|" + File.GetLastWriteTimeUtc(path).Ticks : "";
            if (key == _bgKey) return;
            _bgKey = key;

            if (key.Length == 0)
            {
                uint c = Native.GetSysColor(1); // COLOR_DESKTOP (0x00BBGGRR)
                _root.Background = Frozen(Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF)));
                _bg.Source = null;
                return;
            }
            _bg.Source = BlurredWallpaper(path);
        }
        catch (Exception e)
        {
            Log.Write(e);
            _bg.Source = null;
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
            var t = new Tile(w) { ZIndex = index, Mru = Mru.Of(w.Hwnd) };

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
        _apps.Clear();
        if (_sel is AppTile) _sel = null;
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
        if (!SuggestionsOn || !Searching || _search.Text.Trim().Length < 2) { ClearChips(); return; }
        _suggestTimer.Start();
    }

    async void FetchSuggestions()
    {
        string query = _search.Text.Trim();
        _suggestCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _suggestCts = cts;
        List<string> list;
        try { list = await WebSuggest.FetchAsync(query, MaxChips, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception e) { Log.Write("suggestions: " + e.Message); return; }

        // Ignore answers that arrive after the user typed more, closed the overview, or opened the grid.
        if (cts.IsCancellationRequested || _state != State.Open || _gridMode || _search.Text.Trim() != query) return;
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
        double top = AppsTop + AppTileH + 12;
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
        WebSearch.Run(chip.Text);
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
        action(); // while we're still foreground, so the new window may take focus
        Dismiss(null);
    }

    void OpenMenu(ContextMenu menu, FrameworkElement target, bool atMouse)
    {
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
        if (_state != State.Open || a.IsWeb) return;
        LockHover();
        Select(a);
        var app = a.App;
        string? file = ShellActions.ResolveFile(app);
        bool store = ShellActions.IsStoreApp(app);

        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("Open", "", () => LaunchApp(a)));
        if (!store)
            menu.Items.Add(MenuEntry("Run as administrator", "",
                () => RunAndClose(() => ShellActions.RunAsAdmin(file ?? AppCatalog.ParsingName(app)))));
        if (ShellActions.CanOpenLocation(file))
            menu.Items.Add(MenuEntry("Open file location", "", () => RunAndClose(() => ShellActions.OpenFileLocation(file!))));
        menu.Items.Add(new Separator());
        if (file != null)
            menu.Items.Add(MenuEntry("Properties", "", () => RunAndClose(() => ShellActions.ShowProperties(file))));
        menu.Items.Add(MenuEntry("Uninstall…", "", () => RunAndClose(ShellActions.OpenInstalledApps)));
        OpenMenu(menu, a.Root, atMouse);
    }

    void ShowWindowMenu(Tile t, bool atMouse)
    {
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
        var source = a.IsWeb ? WebSearch.BrowserIcon() : AppCatalog.Icon(a.App, 96);
        FrameworkElement img;
        if (source != null)
        {
            img = new Image { Source = source };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        }
        else
        {
            img = new TextBlock
            {
                Text = a.IsWeb ? "" : "", // globe / generic app
                FontFamily = IconFont,
                FontSize = 36,
                Foreground = TextBrush,
                TextAlignment = TextAlignment.Center
            };
        }
        img.Width = 52;
        img.Height = 52;
        img.Margin = new Thickness(0, 12, 0, 7);
        img.HorizontalAlignment = HorizontalAlignment.Center;

        string label = a.IsWeb ? $"Search the web for “{_search.Text.Trim()}”" : a.App.Name;
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
            ToolTip = a.IsWeb ? "Search in your default browser" : a.App.Name
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

        double topDip = _apps.Count > 0 ? AppsTop + AppTileH + 24 : AppsTop + 6;
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
        _empty.Visibility = Searching && visible.Count == 0 && _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _animMs = SystemParameters.ClientAreaAnimation ? ms : 0;
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

        var tokens = Search.Tokenize(_search.Text);
        Tile? bestTile = null;
        foreach (var t in _tiles)
        {
            if (tokens.Length == 0) { t.Visible = true; t.Score = 0; continue; }
            t.Score = Math.Max(Search.Score(tokens, t.Win.Title),
                      Math.Max(Search.Score(tokens, t.Win.AppName),
                               Search.Score(tokens, t.Win.ProcName) * 0.9));
            t.Visible = t.Score > 0;
            if (t.Visible && (bestTile == null || t.Score > bestTile.Score)) bestTile = t;
        }

        ClearApps();
        if (tokens.Length > 0)
        {
            var matches = AppCatalog.Apps
                .Select(a => (a, s: Search.Score(tokens, a.Name)))
                .Where(m => m.s > 0)
                .OrderByDescending(m => m.s)
                .ThenBy(m => m.a.Name.Length)
                .Take(MaxAppResults - 1);
            foreach (var (a, s) in matches)
            {
                var tile = new AppTile(a, s);
                CreateAppVisuals(tile, _stage);
                _apps.Add(tile);
            }
            // Always offer a web search as the last result.
            var web = new AppTile(WebEntry, 0) { IsWeb = true };
            CreateAppVisuals(web, _stage);
            _apps.Add(web);
        }

        Relayout();
        StartAnim(FilterMs, 1, null);
        QueueSuggestions();

        // Default selection (what Enter does): the best window or app match, unless nothing
        // matches well, e.g. "how many days until christmas", then the web search.
        if (tokens.Length == 0) { SelectDefault(); return; } // search cleared: back to the previous window
        var bestApp = _apps.FirstOrDefault(a => !a.IsWeb); // already sorted best-first
        double appScore = bestApp?.Score ?? 0, winScore = bestTile?.Score ?? 0;
        if (Math.Max(appScore, winScore) < WebThreshold) Select(_apps.First(a => a.IsWeb));
        else if (bestTile != null && winScore >= appScore) Select(bestTile);
        else Select(bestApp);
    }

    // ------------------------------------------------------------------ selection & input

    /// <summary>
    /// Tab / scroll order. Without a search: windows from most to least recently used, like Alt+Tab.
    /// With a search: app results, then windows from best to worst match.
    /// </summary>
    List<object> Selectables()
    {
        if (_gridMode) return new List<object>(_gridApps);
        var list = new List<object>(_apps);
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
        Dismiss(t);
    }

    void LaunchApp(AppTile a)
    {
        if (_state != State.Open) return;
        // Launch while we're still foreground, so the new app or browser may take focus.
        if (a.IsWeb) WebSearch.Run(_search.Text);
        else AppCatalog.Launch(a.App);
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
