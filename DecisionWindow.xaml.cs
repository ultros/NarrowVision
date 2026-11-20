using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace NarrowVision;

public partial class DecisionWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private long _deadline;
    private long _readyAfter;

    public DecisionWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) => _timer.Stop();
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            Native.MakeNonActivating(handle, transparent: false);
            HwndSource.FromHwnd(handle)?.AddHook(HandleMessage);
        };
    }

    internal event Action<bool>? Decided;
    internal event Action? Expired;
    internal bool CanProceed => ProceedButton.IsEnabled && IsVisible;

    internal void Present(ClickAttempt attempt, TargetDetails details, string application, int seconds,
        bool confirmShortcut, bool cancelShortcut)
    {
        LocationText.Text = attempt.Button == MouseButton.Middle ? $"{application} · Middle click" : application;
        TargetName.Text = details.Kind == TargetKind.Unknown ? "Target details unavailable" :
            string.IsNullOrEmpty(details.Name) ? "Unnamed target" : details.Name;
        bool isLink = details.Role.Equals("link", StringComparison.OrdinalIgnoreCase) ||
                      details.Role.Equals("hyperlink", StringComparison.OrdinalIgnoreCase);
        TargetNote.Text = isLink ? "Link destination unavailable through Windows." :
            details.Kind == TargetKind.Unknown ? "The click location will be checked again before proceeding." :
            string.IsNullOrEmpty(details.Role) ? "Desktop click" : details.Role;
        KeyHint.Text = (cancelShortcut, confirmShortcut) switch
        {
            (true, true) => "Esc to cancel  ·  Ctrl+Alt+Enter to proceed",
            (false, true) => "Ctrl+Alt+Enter to proceed",
            (true, false) => "Esc to cancel",
            _ => "Choose an action above"
        };

        _readyAfter = Environment.TickCount64 + 550;
        _deadline = Environment.TickCount64 + seconds * 1000;
        var desiredSize = new Size(Width, Height);
        var handle = new WindowInteropHelper(this).EnsureHandle();
        var bounds = GetPlacement(handle, attempt.Target.Position, desiredSize);
        Native.PlaceTopmost(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        Show();
        Native.PlaceTopmost(handle, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        Tick();
        _timer.Start();
    }

    private static System.Drawing.Rectangle GetPlacement(nint handle, Native.Point point, Size desiredSize)
    {
        var work = Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y)).WorkingArea;
        // Assign the monitor first so Windows can calculate the popup's per-monitor DPI.
        Native.MoveTopmost(handle, work.Left, work.Top);
        double scale = Math.Max(96u, Native.GetDpiForWindow(handle)) / 96.0;
        int width = Math.Min(work.Width, (int)Math.Ceiling(desiredSize.Width * scale));
        int height = Math.Min(work.Height, (int)Math.Ceiling(desiredSize.Height * scale));
        int gap = (int)(18 * scale);
        int x = point.X + gap + width <= work.Right ? point.X + gap : point.X - gap - width;
        int y = point.Y + gap + height <= work.Bottom ? point.Y + gap : point.Y - gap - height;
        return new(Math.Clamp(x, work.Left, work.Right - width),
            Math.Clamp(y, work.Top, work.Bottom - height), width, height);
    }

    private void Tick()
    {
        long now = Environment.TickCount64;
        if (now >= _deadline)
        {
            Expired?.Invoke();
            return;
        }
        ProceedButton.IsEnabled = now >= _readyAfter;
        Countdown.Text = $"Cancels in {Math.Ceiling((_deadline - now) / 1000.0):0}s";
    }

    private nint HandleMessage(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != Native.WmMouseActivate)
            return 0;
        handled = true;
        return 3; // MA_NOACTIVATE preserves the foreground window being reviewed.
    }

    private void CancelClicked(object sender, RoutedEventArgs args) => Decided?.Invoke(false);
    private void ProceedClicked(object sender, RoutedEventArgs args) => Decided?.Invoke(true);
}
