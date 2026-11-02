using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overview;

/// <summary>The installer wizard, shown when the exe is named like "Overview-Setup.exe".</summary>
internal sealed class SetupWindow : Window
{
    static readonly Brush Text = Brush(0xF2, 0xFF, 0xFF, 0xFF);
    static readonly Brush Dim = Brush(0x99, 0xFF, 0xFF, 0xFF);
    static readonly Brush Accent = Brush(0xFF, 0x35, 0x84, 0xE4);

    const string Styles = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style TargetType="Button">
    <Setter Property="Foreground" Value="#F2FFFFFF"/>
    <Setter Property="Background" Value="#2EFFFFFF"/>
    <Setter Property="Padding" Value="20,8"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Cursor" Value="Hand"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Button">
          <Border x:Name="Bd" Background="{TemplateBinding Background}" CornerRadius="6" Padding="{TemplateBinding Padding}">
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Bd" Property="Opacity" Value="0.85"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Bd" Property="Opacity" Value="0.7"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Bd" Property="Opacity" Value="0.4"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="CheckBox">
    <Setter Property="Foreground" Value="#F2FFFFFF"/>
    <Setter Property="FontSize" Value="13.5"/>
    <Setter Property="Margin" Value="0,5,0,5"/>
    <Setter Property="Cursor" Value="Hand"/>
  </Style>
</ResourceDictionary>
""";

    readonly CheckBox _startup, _updates, _desktop, _launch;
    readonly Border _updateBar;
    readonly TextBlock _updateText;
    readonly Button _getNewer, _install, _cancel, _finish;
    readonly StackPanel _optionsPage, _donePage;
    readonly CancellationTokenSource _cts = new();
    Release? _newer;

    public SetupWindow()
    {
        Title = "Overview Setup";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush(0xFF, 0x20, 0x20, 0x20);
        Foreground = Text;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        try { Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Styles)); }
        catch (Exception e) { Log.Write(e); }

        var iconImage = LoadIcon();
        if (iconImage != null) Icon = iconImage;

        // Header: icon, name, version
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var logo = new Image { Source = iconImage, Width = 56, Height = 56, Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(logo, Dock.Left);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Overview", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Text });
        titles.Children.Add(new TextBlock { Text = $"Version {Updater.Current} · GNOME-style Activities overview for Windows", FontSize = 12.5, Foreground = Dim });
        header.Children.Add(logo);
        header.Children.Add(titles);

        // "A newer version is available" bar (filled in when the check finds one)
        _updateText = new TextBlock { Foreground = Text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _getNewer = new Button { Content = "Get the newer version", Background = Accent, Margin = new Thickness(12, 0, 0, 0) };
        _getNewer.Click += async (_, _) => await GetNewerAsync();
        var barDock = new DockPanel();
        DockPanel.SetDock(_getNewer, Dock.Right);
        barDock.Children.Add(_getNewer);
        barDock.Children.Add(_updateText);
        _updateBar = new Border
        {
            Background = Brush(0x33, 0x35, 0x84, 0xE4),
            BorderBrush = Brush(0x66, 0x35, 0x84, 0xE4),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 10, 10),
            Margin = new Thickness(0, 0, 0, 16),
            Child = barDock,
            Visibility = Visibility.Collapsed
        };

        // Options page
        bool installed = Installer.IsInstalled;
        var current = Settings.Load();
        _startup = new CheckBox { Content = "Start with Windows", IsChecked = installed ? AutoStart.IsEnabled() : true };
        _updates = new CheckBox
        {
            Content = "Check for updates automatically (asks before installing)",
            IsChecked = installed ? current.CheckForUpdates : true
        };
        _desktop = new CheckBox { Content = "Create a desktop shortcut", IsChecked = installed && Installer.HasDesktopShortcut };

        _optionsPage = new StackPanel();
        _optionsPage.Children.Add(new TextBlock
        {
            Text = installed
                ? "Overview is already installed. Setup will update it and keep your settings."
                : "Tap the Windows key to see all your windows, search apps and the web, and double-tap to switch back.",
            Foreground = Text,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        _optionsPage.Children.Add(_startup);
        _optionsPage.Children.Add(_updates);
        _optionsPage.Children.Add(_desktop);
        _optionsPage.Children.Add(new TextBlock
        {
            Text = $"Installs for your user only (no admin needed) to:\n{Installer.InstallDir}",
            Foreground = Dim,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        });

        // Done page
        _launch = new CheckBox { Content = "Start Overview now", IsChecked = true };
        _donePage = new StackPanel { Visibility = Visibility.Collapsed };
        _donePage.Children.Add(new TextBlock { Text = "Overview is installed", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Text, Margin = new Thickness(0, 0, 0, 6) });
        _donePage.Children.Add(new TextBlock
        {
            Text = "Tap the Windows key to open it. It's in the Start menu and the system tray, and you can uninstall it from Settings → Apps → Installed apps.",
            Foreground = Text,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        _donePage.Children.Add(_launch);

        // Buttons
        _cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 10, 0) };
        _cancel.Click += (_, _) => Close();
        _install = new Button { Content = installed ? "Update" : "Install", Background = Accent, IsDefault = true };
        _install.Click += async (_, _) => await InstallAsync();
        _finish = new Button { Content = "Finish", Background = Accent, Visibility = Visibility.Collapsed };
        _finish.Click += (_, _) =>
        {
            if (_launch.IsChecked == true) Installer.Launch();
            Close();
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_install);
        buttons.Children.Add(_finish);

        var root = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
        root.Children.Add(header);
        root.Children.Add(_updateBar);
        root.Children.Add(_optionsPage);
        root.Children.Add(_donePage);
        root.Children.Add(buttons);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            int on = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        };
        Loaded += async (_, _) => await CheckForNewerAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    static BitmapFrame? LoadIcon()
    {
        try
        {
            var s = typeof(SetupWindow).Assembly.GetManifestResourceStream("overview.ico");
            return s == null ? null : BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch { return null; }
    }

    /// <summary>The installer checks GitHub too, so nobody installs an outdated version by accident.</summary>
    async Task CheckForNewerAsync()
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var latest = await Updater.GetLatestAsync(timeout.Token);
            if (latest == null || !Updater.IsNewer(latest) || !IsLoaded) return;
            _newer = latest;
            _updateText.Text = $"Version {latest.Version} is available. This setup is version {Updater.Current}.";
            _updateBar.Visibility = Visibility.Visible;
        }
        catch { /* offline: just install this version */ }
    }

    async Task GetNewerAsync()
    {
        if (_newer == null) return;
        _getNewer.IsEnabled = false;
        _install.IsEnabled = false;
        _getNewer.Content = "Downloading…";
        try
        {
            string path = await Updater.DownloadSetupAsync(_newer, _cts.Token);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); // opens the newer setup
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Write(e);
            MessageBox.Show(this, "Couldn't download the newer version:\n\n" + e.Message, "Overview Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            _getNewer.Content = "Get the newer version";
            _getNewer.IsEnabled = true;
            _install.IsEnabled = true;
        }
    }

    async Task InstallAsync()
    {
        var options = new InstallOptions
        {
            StartWithWindows = _startup.IsChecked == true,
            CheckForUpdates = _updates.IsChecked == true,
            DesktopShortcut = _desktop.IsChecked == true
        };
        _install.IsEnabled = false;
        _cancel.IsEnabled = false;
        _getNewer.IsEnabled = false;
        _install.Content = "Installing…";
        try
        {
            await Task.Run(() => Installer.Install(options));
            _optionsPage.Visibility = Visibility.Collapsed;
            _updateBar.Visibility = Visibility.Collapsed;
            _install.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
            _donePage.Visibility = Visibility.Visible;
            _finish.Visibility = Visibility.Visible;
            _finish.Focus();
        }
        catch (Exception e)
        {
            Log.Write(e);
            MessageBox.Show(this, "Installation failed:\n\n" + e.Message, "Overview Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            _install.Content = Installer.IsInstalled ? "Update" : "Install";
            _install.IsEnabled = true;
            _cancel.IsEnabled = true;
            _getNewer.IsEnabled = true;
        }
    }
}
