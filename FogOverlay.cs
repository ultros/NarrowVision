using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace NarrowVision;

internal static class FogDrawing
{
    private static readonly Pen Guide = CreateGuide();

    public static void Draw(DrawingContext dc, Rect canvas, Point center, Settings settings, double scale = 1)
    {
        double opacity = settings.Darkness / 100.0;
        double feather = settings.Feather * scale;
        double width = settings.Width * scale;
        double height = settings.Height * scale;
        bool reveal = settings.FogMode == FogMode.Reveal;
        var cutout = new Rect(center.X - width / 2, center.Y - height / 2, width, height);
        if (settings.Shape == ApertureShape.Ellipse && feather > 0)
        {
            DrawSoftEllipse(dc, canvas, cutout, center, feather, opacity, reveal, settings.Outline);
            return;
        }

        Rect affected = cutout;
        affected.Inflate(feather + 2, feather + 2);
        if (!affected.IntersectsWith(canvas))
        {
            if (reveal)
                dc.DrawRectangle(Shade(opacity), null, canvas);
            return;
        }

        dc.PushClip(new RectangleGeometry(canvas));
        Geometry aperture = ShapeAt(cutout, settings.Shape, 0);
        if (opacity > 0)
        {
            int bands = feather == 0 ? 1 : Math.Clamp((int)Math.Ceiling(feather / 3), 5, 24);
            Geometry screen = new RectangleGeometry(canvas);
            double previousOpacity = 0;
            for (int band = 1; band <= bands; band++)
            {
                double progress = (double)band / bands;
                double blend = progress * progress * (3 - 2 * progress);
                double nextOpacity = opacity * blend;
                double expansion = reveal ? feather * (band - 1) / bands : feather * (1 - progress);
                Geometry shape = expansion == 0 ? aperture : ShapeAt(cutout, settings.Shape, expansion);
                Geometry region = reveal ? new CombinedGeometry(GeometryCombineMode.Exclude, screen, shape) : shape;
                dc.DrawGeometry(Shade((nextOpacity - previousOpacity) / (1 - previousOpacity)), null, region);
                previousOpacity = nextOpacity;
            }
        }
        if (settings.Outline)
            dc.DrawGeometry(null, Guide, aperture);
        dc.Pop();
    }

    private static void DrawSoftEllipse(DrawingContext dc, Rect canvas, Rect cutout, Point center,
        double feather, double opacity, bool reveal, bool outline)
    {
        if (opacity > 0)
        {
            double expansion = 1 + 2 * feather / Math.Min(cutout.Width, cutout.Height);
            var brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = center,
                RadiusX = cutout.Width * expansion / 2,
                RadiusY = cutout.Height * expansion / 2
            };
            double inner = 1 / expansion;
            for (int index = 0; index <= 8; index++)
            {
                double progress = index / 8.0;
                double blend = progress * progress * (3 - 2 * progress);
                byte alpha = (byte)Math.Round(255 * opacity * (reveal ? blend : 1 - blend));
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, 0, 0, 0), inner + (1 - inner) * progress));
            }
            dc.DrawRectangle(brush, null, canvas);
        }
        if (outline)
        {
            dc.PushClip(new RectangleGeometry(canvas));
            dc.DrawGeometry(null, Guide, new EllipseGeometry(cutout));
            dc.Pop();
        }
    }

    private static Geometry ShapeAt(Rect bounds, ApertureShape shape, double grow)
    {
        double radius = Math.Min(32, Math.Min(bounds.Width, bounds.Height) * 0.18) + grow;
        bounds.Inflate(grow, grow);
        return shape == ApertureShape.Ellipse
            ? new EllipseGeometry(bounds)
            : new RectangleGeometry(bounds, radius, radius);
    }

    private static SolidColorBrush Shade(double alpha)
        => new(Color.FromArgb((byte)Math.Round(alpha * 255), 0, 0, 0));

    private static Pen CreateGuide()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(185, 139, 202, 253)), 1);
        pen.Freeze();
        return pen;
    }
}

internal sealed class FogOverlay : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _frame;
    private OverlayWindow[] _windows = [];
    private Settings? _settings;
    private Native.Point? _lastPoint;
    private bool _disposed;

    public FogOverlay(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _frame = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _frame.Tick += (_, _) => Render();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Apply(Settings settings, bool enabled)
    {
        Settings? value = enabled ? settings : null;
        if (_settings == value)
            return;

        bool needsWindows = _settings is null && enabled;
        _settings = value;
        _lastPoint = null;
        if (!enabled)
        {
            _frame.Stop();
            CloseWindows();
            return;
        }
        if (needsWindows)
            Rebuild();
        Render();
        _frame.Start();
    }

    private void Render()
    {
        if (_settings is not { } settings || !Native.GetCursorPos(out var point) || point == _lastPoint)
            return;
        _lastPoint = point;

        foreach (var window in _windows)
        {
            bool wanted = settings.Monitors == MonitorScope.All || window.Screen.Bounds.Contains(point.X, point.Y);
            window.Update(point, settings, wanted);
        }
    }

    private void Rebuild()
    {
        CloseWindows();
        _windows = Forms.Screen.AllScreens.Select(screen => new OverlayWindow(screen)).ToArray();
        _lastPoint = null;
    }

    private void CloseWindows()
    {
        foreach (var window in _windows)
            window.Close();
        _windows = [];
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs args)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_settings is null || _disposed)
                return;
            Rebuild();
            Render();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _frame.Stop();
        CloseWindows();
    }

    private sealed class OverlayWindow : Window
    {
        private readonly FogSurface _surface = new();
        private nint _handle;

        public OverlayWindow(Forms.Screen screen)
        {
            Screen = screen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false;
            ShowInTaskbar = false;
            Focusable = false;
            Topmost = true;
            Width = Height = 1;
            Content = _surface;
            SourceInitialized += (_, _) =>
            {
                _handle = new WindowInteropHelper(this).Handle;
                Native.MakeNonActivating(_handle, transparent: true);
                Place();
            };
        }

        public Forms.Screen Screen { get; }

        public void Update(Native.Point point, Settings settings, bool wanted)
        {
            if (!wanted)
            {
                if (IsVisible)
                    Hide();
                return;
            }
            if (!IsVisible)
            {
                Show();
                Place();
            }

            var deviceTransform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                ?? Matrix.Identity;
            var bounds = Screen.Bounds;
            _surface.Center = deviceTransform.Transform(new Point(point.X - bounds.Left, point.Y - bounds.Top));
            _surface.Settings = settings;
            _surface.InvalidateVisual();
        }

        private void Place()
        {
            var bounds = Screen.Bounds;
            Native.PlaceTopmost(_handle, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
    }

    private sealed class FogSurface : FrameworkElement
    {
        public Point Center { get; set; }
        public Settings? Settings { get; set; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (Settings is { } settings && ActualWidth > 0 && ActualHeight > 0)
                FogDrawing.Draw(drawingContext, new Rect(RenderSize), Center, settings);
        }
    }
}
