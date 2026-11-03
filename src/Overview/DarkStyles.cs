using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Overview;

/// <summary>Shared look for Overview's normal windows (setup and settings): dark, rounded, Adwaita blue.</summary>
internal static class DarkStyles
{
    public static readonly Brush Text = Brush(0xF2, 0xFF, 0xFF, 0xFF);
    public static readonly Brush Dim = Brush(0x99, 0xFF, 0xFF, 0xFF);
    public static readonly Brush Accent = Brush(0xFF, 0x35, 0x84, 0xE4);
    public static readonly Brush WindowBackground = Brush(0xFF, 0x20, 0x20, 0x20);
    public static readonly Brush Card = Brush(0xFF, 0x2A, 0x2A, 0x2A);

    const string Xaml = """
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
  <Style TargetType="RadioButton">
    <Setter Property="Foreground" Value="#F2FFFFFF"/>
    <Setter Property="FontSize" Value="13.5"/>
    <Setter Property="Margin" Value="0,0,18,6"/>
    <Setter Property="Cursor" Value="Hand"/>
  </Style>
</ResourceDictionary>
""";

    static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Gives a window the dark look: styles, background, font, dark title bar and the app icon.</summary>
    public static void Apply(Window w)
    {
        try { w.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Xaml)); }
        catch (Exception e) { Log.Write(e); }
        w.Background = WindowBackground;
        w.Foreground = Text;
        w.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        w.UseLayoutRounding = true;
        var icon = LoadIcon();
        if (icon != null) w.Icon = icon;
        w.SourceInitialized += (_, _) =>
        {
            int on = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        };
    }

    public static BitmapFrame? LoadIcon()
    {
        try
        {
            var s = typeof(DarkStyles).Assembly.GetManifestResourceStream("overview.ico");
            return s == null ? null : BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch { return null; }
    }
}
