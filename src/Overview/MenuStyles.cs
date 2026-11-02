using System;
using System.Windows;
using System.Windows.Markup;

namespace Overview;

/// <summary>Dark, rounded right-click menus that match the overview.</summary>
internal static class MenuStyles
{
    const string Xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style TargetType="ContextMenu">
    <Setter Property="SnapsToDevicePixels" Value="True"/>
    <Setter Property="HasDropShadow" Value="False"/>
    <Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ContextMenu">
          <Border Background="#F2262626" BorderBrush="#33FFFFFF" BorderThickness="1" CornerRadius="10" Padding="5">
            <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType="MenuItem">
    <Setter Property="Foreground" Value="#F2FFFFFF"/>
    <Setter Property="FontSize" Value="13"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="MenuItem">
          <Border x:Name="Bd" Background="Transparent" CornerRadius="6" Padding="10,7,22,7" MinWidth="210">
            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="28"/>
                <ColumnDefinition/>
              </Grid.ColumnDefinitions>
              <ContentPresenter ContentSource="Icon" VerticalAlignment="Center" HorizontalAlignment="Left"/>
              <ContentPresenter Grid.Column="1" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsHighlighted" Value="True">
              <Setter TargetName="Bd" Property="Background" Value="#2EFFFFFF"/>
            </Trigger>
            <Trigger Property="IsEnabled" Value="False">
              <Setter Property="Opacity" Value="0.4"/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key="{x:Static MenuItem.SeparatorStyleKey}" TargetType="Separator">
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="Separator">
          <Border Height="1" Margin="8,4" Background="#26FFFFFF"/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";

    public static void Install()
    {
        try
        {
            if (Application.Current == null) return;
            Application.Current.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Xaml));
        }
        catch (Exception e) { Log.Write(e); } // menus fall back to the default look
    }
}
