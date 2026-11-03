using System;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Overview;

/// <summary>Settings → every option in one place. Changes apply and save at once.</summary>
internal sealed class SettingsWindow : Window
{
    static SettingsWindow? _open;

    /// <summary>Shows the window, or brings it forward if it's already open.</summary>
    public static void ShowSingle()
    {
        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }
        _open = new SettingsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    static Settings S => App.Settings;
    static App? Host => Application.Current as App;

    readonly TextBlock _resultsLabel, _dimLabel;

    SettingsWindow()
    {
        Title = "Overview Settings";
        Width = 560;
        Height = 720;
        MinWidth = 460;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DarkStyles.Apply(this);

        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };

        // Header
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var logo = new Image { Source = DarkStyles.LoadIcon(), Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(logo, Dock.Left);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Settings", FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = DarkStyles.Text });
        titles.Children.Add(new TextBlock { Text = $"Overview {Updater.Current} · changes are saved automatically", FontSize = 12.5, Foreground = DarkStyles.Dim });
        header.Children.Add(logo);
        header.Children.Add(titles);
        root.Children.Add(header);

        // ---------------------------------------------------------------- General
        var general = Section(root, "General");
        general.Children.Add(Check("Tap the Windows key to open Overview", S.SuperKey, v => { S.SuperKey = v; Changed(); },
            "Turn off to get the Start menu back on the Windows key."));
        general.Children.Add(Check("Tapping it again switches to the highlighted window", S.SecondTapSwitches, v => { S.SecondTapSwitches = v; Changed(); },
            "Like Alt+Tab: double-tap jumps back to your previous window. Off: a second tap just closes Overview."));
        general.Children.Add(Check("Hot corner: push the mouse into the top-left corner to open", S.HotCorner, v => { S.HotCorner = v; Changed(); }));
        general.Children.Add(Check("Start with Windows", AutoStart.IsEnabled(), v =>
        {
            ThreadPool.QueueUserWorkItem(_ => AutoStart.Set(v)); // runs schtasks: off the UI thread
        }));
        if (Installer.IsInstalledCopy)
        {
            var updates = new DockPanel();
            var check = new Button { Content = "Check now", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            check.Click += (_, _) => Host?.CheckForUpdatesNow();
            DockPanel.SetDock(check, Dock.Right);
            updates.Children.Add(check);
            updates.Children.Add(Check("Check for updates automatically", S.CheckForUpdates, v => { S.CheckForUpdates = v; Changed(); },
                "Asks before installing anything."));
            general.Children.Add(updates);
        }
        else
        {
            general.Children.Add(Note("Updates: this copy isn't installed, so it doesn't update itself. Use Overview-Setup.exe to install."));
        }

        // ---------------------------------------------------------------- Search
        var search = Section(root, "Search");
        search.Children.Add(Check("Show web search suggestions while typing", S.WebSuggestions, v => { S.WebSuggestions = v; Changed(); },
            "Sends what you type in Overview to the search engine, like a browser's address bar does."));

        search.Children.Add(Label("Web search engine"));
        search.Children.Add(Choice("engine", new[]
        {
            ("My browser's default", "default"), ("Google", "google"), ("Bing", "bing"),
            ("DuckDuckGo", "duckduckgo"), ("Brave Search", "brave")
        }, S.SearchEngine, v => { S.SearchEngine = v; Changed(); }));

        var learn = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var clear = new Button { Content = "Clear history", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        clear.Click += (_, _) =>
        {
            Usage.Clear();
            clear.Content = "Cleared";
            clear.IsEnabled = false;
        };
        DockPanel.SetDock(clear, Dock.Right);
        learn.Children.Add(clear);
        learn.Children.Add(Check("Rank results by what I use", S.LearnFromUsage, v => { S.LearnFromUsage = v; Changed(); },
            "Results you picked before for the same letters come first, and apps you use often rank higher. Stored only on this PC."));
        search.Children.Add(learn);

        _resultsLabel = Label("");
        search.Children.Add(_resultsLabel);
        search.Children.Add(SliderRow(3, 12, S.MaxAppResults, v =>
        {
            S.MaxAppResults = (int)v;
            _resultsLabel.Text = $"App results shown: {S.MaxAppResults}";
            Changed();
        }));
        _resultsLabel.Text = $"App results shown: {S.MaxAppResults}";

        // ---------------------------------------------------------------- Appearance
        var look = Section(root, "Appearance");
        look.Children.Add(Label("Animations"));
        look.Children.Add(Choice("anim", new[]
        {
            ("Normal", "1"), ("Fast", "0.5"), ("Slow", "1.6"), ("Off", "0")
        }, SpeedKey(S.AnimationSpeed), v => { S.AnimationSpeed = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture); Changed(); }));

        _dimLabel = Label("");
        look.Children.Add(_dimLabel);
        look.Children.Add(SliderRow(0, 80, S.DimPercent, v =>
        {
            S.DimPercent = (int)v;
            _dimLabel.Text = $"Darken the background: {S.DimPercent} %";
            Changed();
        }));
        _dimLabel.Text = $"Darken the background: {S.DimPercent} %";

        // ---------------------------------------------------------------- Footer
        var footer = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        var close = new Button { Content = "Close", Background = DarkStyles.Accent, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        var link = new TextBlock
        {
            Text = "Report a problem or suggest something on GitHub",
            Foreground = DarkStyles.Accent,
            FontSize = 12.5,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = TextDecorations.Underline
        };
        link.MouseLeftButtonUp += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(Updater.RepoUrl + "/issues") { UseShellExecute = true }); }
            catch (Exception e) { Log.Write(e); }
        };
        footer.Children.Add(close);
        footer.Children.Add(link);
        root.Children.Add(footer);

        var scroller = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SmoothScroll.Attach(scroller); // WPF's own wheel scrolling is far too fast with touchpads and fine-step wheels
        Content = scroller;
    }

    static string SpeedKey(double speed) => speed switch
    {
        <= 0 => "0",
        < 0.75 => "0.5",
        > 1.3 => "1.6",
        _ => "1"
    };

    /// <summary>Saves, and applies the settings that need more than a re-read.</summary>
    static void Changed()
    {
        S.Save();
        Host?.ApplySettings();
    }

    // ---------------------------------------------------------------- building blocks

    static StackPanel Section(Panel root, string title)
    {
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = DarkStyles.Text,
            Margin = new Thickness(0, 20, 0, 8)
        });
        var body = new StackPanel();
        root.Children.Add(new Border
        {
            Background = DarkStyles.Card,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10, 16, 12),
            Child = body
        });
        return body;
    }

    static FrameworkElement Check(string text, bool value, Action<bool> changed, string? hint = null)
    {
        var box = new CheckBox { IsChecked = value };
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = DarkStyles.Text });
        if (hint != null)
            content.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = DarkStyles.Dim, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        box.Content = content;
        box.VerticalContentAlignment = VerticalAlignment.Top;
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    static TextBlock Label(string text) =>
        new() { Text = text, Foreground = DarkStyles.Text, FontSize = 13.5, Margin = new Thickness(0, 10, 0, 6) };

    static TextBlock Note(string text) =>
        new() { Text = text, Foreground = DarkStyles.Dim, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

    static FrameworkElement Choice(string group, (string Label, string Value)[] options, string current, Action<string> changed)
    {
        var panel = new WrapPanel();
        foreach (var (label, value) in options)
        {
            var radio = new RadioButton { Content = label, GroupName = group, IsChecked = value == current };
            radio.Checked += (_, _) => changed(value);
            panel.Children.Add(radio);
        }
        return panel;
    }

    static FrameworkElement SliderRow(double min, double max, double value, Action<double> changed)
    {
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            IsSnapToTickEnabled = true,
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = Math.Max(1, (max - min) / 8)
        };
        slider.ValueChanged += (_, e) => changed(e.NewValue);
        return slider;
    }
}
