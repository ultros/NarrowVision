using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;

namespace NarrowVision;

public partial class SettingsWindow : Window
{
    private bool _syncing = true;

    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    internal event Action<Settings>? SettingsChanged;
    internal event Action? PauseRequested;
    internal event Action<bool>? StartupRequested;
    internal event Action? SaveProfileRequested;
    internal event Action? LoadProfileRequested;
    internal event Action? ClearProfileRequested;
    internal event Action<string>? StatusChanged;

    internal void SetStartup(bool registered, bool available)
    {
        StartWithWindows.IsChecked = registered;
        StartWithWindows.IsEnabled = available;
    }

    internal void SetProfile(bool saved)
    {
        ProfileStatus.Text = saved ? "Saved default loads each time NarrowVision opens." : "Current settings are saved automatically.";
        LoadProfile.IsEnabled = saved;
        ClearProfile.IsEnabled = saved;
    }

    internal void LoadSettings(Settings settings)
    {
        _syncing = true;
        FogSwitch.IsChecked = settings.FogEnabled;
        Mode.SelectedIndex = (int)settings.FogMode;
        Shape.SelectedIndex = (int)settings.Shape;
        Monitors.SelectedIndex = (int)settings.Monitors;
        FogWidth.Value = settings.Width;
        FogHeight.Value = settings.Height;
        Darkness.Value = settings.Darkness;
        Feather.Value = settings.Feather;
        Outline.IsChecked = settings.Outline;
        GuardSwitch.IsChecked = settings.GuardEnabled;
        Protection.SelectedIndex = (int)settings.GuardMode;
        Middle.IsChecked = settings.GuardMiddleButton;
        Timeout.Value = settings.DecisionSeconds;
        _syncing = false;
        UpdatePreview(settings);
    }

    internal void SetState(string status, bool paused, bool guardAvailable,
        string fogKey, string guardKey, string settingsKey, string pauseKey)
    {
        StatusText.Text = status;
        PauseButton.Content = paused ? "Resume now" : "Pause for 5 min";
        GuardSwitch.IsEnabled = guardAvailable;
        FogShortcut.Text = $"Fog overlay  ·  {fogKey}";
        GuardShortcut.Text = $"Click Guard  ·  {guardKey}";
        SettingsShortcut.Text = $"Open settings  ·  {settingsKey}";
        PauseShortcut.Text = $"Pause / resume  ·  {pauseKey}";
    }

    private void SettingChanged(object sender, RoutedEventArgs args)
    {
        if (_syncing)
            return;
        var settings = ReadSettings();
        UpdatePreview(settings);
        SettingsChanged?.Invoke(settings);
    }

    private Settings ReadSettings() => new()
    {
        FogEnabled = FogSwitch.IsChecked == true,
        FogMode = (FogMode)Mode.SelectedIndex,
        Shape = (ApertureShape)Shape.SelectedIndex,
        Monitors = (MonitorScope)Monitors.SelectedIndex,
        Width = (int)FogWidth.Value,
        Height = (int)FogHeight.Value,
        Darkness = (int)Darkness.Value,
        Feather = (int)Feather.Value,
        Outline = Outline.IsChecked == true,
        GuardEnabled = GuardSwitch.IsChecked == true,
        GuardMode = (GuardMode)Protection.SelectedIndex,
        GuardMiddleButton = Middle.IsChecked == true,
        DecisionSeconds = (int)Timeout.Value
    };

    private void UpdatePreview(Settings settings)
    {
        WidthValue.Text = $"{settings.Width} DIP";
        HeightValue.Text = $"{settings.Height} DIP";
        DarknessValue.Text = $"{settings.Darkness}%";
        FeatherValue.Text = $"{settings.Feather} DIP";
        TimeoutValue.Text = $"{settings.DecisionSeconds} sec";
        GuardDescription.Text = settings.GuardMode == GuardMode.All
            ? "Holds each covered click for review. Dragging and modified clicks are canceled."
            : "Asks about actions and unknown targets. Text or document areas can pass through based on the app’s accessibility data.";
        Preview.Update(settings);
    }

    private void PresetClicked(object sender, RoutedEventArgs args)
    {
        var settings = ReadSettings();
        var updated = ((Button)sender).Tag switch
        {
            "Small" => settings with { Width = 280, Height = 220, Feather = 35 },
            "Wide" => settings with { Width = 900, Height = 520, Feather = 80 },
            _ => settings with { Width = 500, Height = 340, Feather = 55 }
        };
        LoadSettings(updated);
        SettingsChanged?.Invoke(updated);
    }

    private void PauseClicked(object sender, RoutedEventArgs args) => PauseRequested?.Invoke();
    private void CloseClicked(object sender, RoutedEventArgs args) => Close();
    private void StartupClicked(object sender, RoutedEventArgs args) => StartupRequested?.Invoke(StartWithWindows.IsChecked == true);
    private void SaveProfileClicked(object sender, RoutedEventArgs args) => SaveProfileRequested?.Invoke();
    private void LoadProfileClicked(object sender, RoutedEventArgs args) => LoadProfileRequested?.Invoke();
    private void ClearProfileClicked(object sender, RoutedEventArgs args) => ClearProfileRequested?.Invoke();

    private void OpenCompanySite(object sender, RequestNavigateEventArgs args)
    {
        args.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            StatusChanged?.Invoke("Windows could not open novusmercatura.com.");
        }
    }
}

public sealed class FogPreview : FrameworkElement
{
    private Settings _settings = new();
    private static readonly Brush Background = MakeBrush(0x284D71);
    private static readonly Brush WindowPanel = MakeBrush(0x4D769B);
    private static readonly Brush Bar = MakeBrush(0x9BB8D7);
    private static readonly Brush Accent = MakeBrush(0xACDFFF);

    internal void Update(Settings settings)
    {
        _settings = settings;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 30 || ActualHeight < 30)
            return;
        var bounds = new Rect(RenderSize);
        dc.PushClip(new RectangleGeometry(bounds, 8, 8));
        dc.DrawRectangle(Background, null, bounds);
        double width = ActualWidth;
        DrawBlock(dc, WindowPanel, width * 0.07, 13, width * 0.86, 101);
        DrawBlock(dc, Bar, width * 0.15, 27, width * 0.29, 7);
        DrawBlock(dc, Bar, width * 0.15, 43, width * 0.64, 3);
        DrawBlock(dc, Bar, width * 0.15, 53, width * 0.54, 3);
        DrawBlock(dc, Bar, width * 0.15, 63, width * 0.60, 3);
        DrawBlock(dc, Bar, width * 0.15, 73, width * 0.45, 3);
        DrawBlock(dc, Accent, width * 0.15, 88, width * 0.25, 11);
        var center = new Point(width * 0.5, ActualHeight * 0.50);
        if (_settings.FogEnabled)
            FogDrawing.Draw(dc, bounds, center, _settings, Math.Min(width / 1280, ActualHeight / 780));
        var pointer = new StreamGeometry();
        using (var path = pointer.Open())
        {
            path.BeginFigure(center, true, true);
            path.LineTo(center + new Vector(13, 10), true, false);
            path.LineTo(center + new Vector(7, 11), true, false);
            path.LineTo(center + new Vector(5, 17), true, false);
        }
        dc.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 1), pointer);
        dc.Pop();
    }

    private static void DrawBlock(DrawingContext dc, Brush brush, double x, double y, double width, double height)
        => dc.DrawRoundedRectangle(brush, null, new Rect(x, y, width, height), 2, 2);

    private static Brush MakeBrush(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
