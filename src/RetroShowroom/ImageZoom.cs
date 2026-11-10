using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using RetroShowroom.Core;
using Windows.Foundation;

namespace RetroShowroom;

/// <summary>Fläche unter dem Bild. Kann den Mauszeiger ändern (Hand zum Verschieben), was nur abgeleitete Klassen dürfen.</summary>
public sealed partial class PanSurface : Grid
{
    public void SetCursor(InputSystemCursorShape? shape) =>
        ProtectedCursor = shape is { } s ? InputSystemCursor.Create(s) : null;
}

/// <summary>
/// Zoomen und Verschieben für das Ergebnisbild (zur Kontrolle von Details).
/// Mausrad zoomt an der Mausposition, Ziehen mit gedrückter Maustaste verschiebt, Doppelklick wechselt
/// zwischen „einpassen“ und Originalgröße (1 Bildpunkt = 1 Bildschirmpunkt). Touch: Zwei-Finger-Zoom und Wischen.
/// Zoomfaktor 1 im ScrollViewer heißt „eingepasst“.
/// </summary>
internal sealed class ImageZoom
{
    private const double ImageMargin = 10;
    private const double WheelStep = 1.2;
    private const double ButtonStep = 1.5;

    private readonly ScrollViewer _scroll;
    private readonly PanSurface _surface;
    private readonly Image _image;
    private readonly FrameworkElement _bar;
    private readonly TextBlock _label;

    private Size _pixelSize;
    private bool _panning;
    private Point _panStart;
    private double _panH, _panV;

    // Ziel der letzten Änderung, bis der ScrollViewer sie übernommen hat (schnelles Drehen am Mausrad)
    private bool _pending;
    private double _targetZoom, _targetH, _targetV;

    public ImageZoom(ScrollViewer scroll, PanSurface surface, Image image, FrameworkElement bar, TextBlock label)
    {
        (_scroll, _surface, _image, _bar, _label) = (scroll, surface, image, bar, label);
        _image.Margin = new Thickness(ImageMargin);

        _scroll.SizeChanged += (_, _) =>
        {
            UpdateSurfaceSize();
            UpdateLimits();
            UpdateLabel();
        };
        _scroll.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate) _pending = false;
            UpdateLabel();
        };

        _surface.PointerWheelChanged += OnWheel;
        _surface.PointerPressed += OnPressed;
        _surface.PointerMoved += OnMoved;
        _surface.PointerReleased += (_, e) => EndPan(e.Pointer);
        _surface.PointerCaptureLost += (_, _) => EndPan(null);
        _surface.PointerCanceled += (_, e) => EndPan(e.Pointer);
        _surface.DoubleTapped += OnDoubleTapped;
        _bar.Visibility = Visibility.Collapsed;
    }

    public bool HasImage => _image.Source is not null && _pixelSize.Width > 0;

    /// <summary>
    /// Zeigt ein Bild. Hat es dieselbe Pixelgröße wie das vorige (z. B. ein neues Rendering mit gleicher Qualität),
    /// bleiben Zoom und Ausschnitt erhalten, sonst wird eingepasst.
    /// </summary>
    public void Show(BitmapImage? bitmap)
    {
        var size = bitmap is null ? default : new Size(bitmap.PixelWidth, bitmap.PixelHeight);
        bool keepView = bitmap is not null && size == _pixelSize;
        _pixelSize = size;
        _image.Source = bitmap;
        _bar.Visibility = bitmap is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateSurfaceSize();
        UpdateLimits();
        if (!keepView) Fit(animate: false);
        UpdateLabel();
    }

    // ---------------------------------------------------------------- Befehle

    public void ZoomIn() => ZoomTo(CurrentZoom * ButtonStep, null, animate: true);
    public void ZoomOut() => ZoomTo(CurrentZoom / ButtonStep, null, animate: true);
    public void ActualSize() => ZoomTo(OneToOne, null, animate: true);

    public void Fit(bool animate = true)
    {
        _pending = true;
        (_targetZoom, _targetH, _targetV) = (1, 0, 0);
        _scroll.ChangeView(0, 0, 1f, !animate);
    }

    // ---------------------------------------------------------------- Rechnen

    private double CurrentZoom => _pending ? _targetZoom : _scroll.ZoomFactor;
    private double CurrentH => _pending ? _targetH : _scroll.HorizontalOffset;
    private double CurrentV => _pending ? _targetV : _scroll.VerticalOffset;

    /// <summary>Breite des Bilds in Bildschirmpunkten (DIP), wenn es eingepasst ist.</summary>
    private double FittedWidth
    {
        get
        {
            if (_pixelSize.Width <= 0 || _pixelSize.Height <= 0) return 1;
            double w = Math.Max(1, _scroll.ActualWidth - 2 * ImageMargin);
            double h = Math.Max(1, _scroll.ActualHeight - 2 * ImageMargin);
            return _pixelSize.Width * Math.Min(w / _pixelSize.Width, h / _pixelSize.Height);
        }
    }

    /// <summary>Zoomfaktor, bei dem ein Bildpunkt genau einem Bildschirmpixel entspricht.</summary>
    private double OneToOne
    {
        get
        {
            double scale = _scroll.XamlRoot?.RasterizationScale ?? 1;
            return Math.Max(1, _pixelSize.Width / (FittedWidth * scale));
        }
    }

    private void UpdateSurfaceSize()
    {
        // Die Fläche ist genau so groß wie der sichtbare Bereich: Zoomfaktor 1 = eingepasst, ohne Scrollbalken
        _surface.Width = Math.Max(1, _scroll.ActualWidth);
        _surface.Height = Math.Max(1, _scroll.ActualHeight);
    }

    private void UpdateLimits()
    {
        // Bis 4-fache Originalgröße, damit man einzelne Pixel klar sieht
        _scroll.MinZoomFactor = 1;
        _scroll.MaxZoomFactor = (float)Math.Clamp(OneToOne * 4, 4, 64);
    }

    private void UpdateLabel()
    {
        if (!HasImage) return;
        double z = _scroll.ZoomFactor;
        _label.Text = $"{z / OneToOne * 100:0} %";
        if (!_panning) _surface.SetCursor(z > 1.001 ? InputSystemCursorShape.Hand : null);
    }

    /// <summary>Zoomt so, dass der Bildpunkt unter <paramref name="anchor"/> (Position im sichtbaren Bereich) an seiner Stelle bleibt.</summary>
    private void ZoomTo(double zoom, Point? anchor, bool animate)
    {
        if (!HasImage) return;
        zoom = Math.Clamp(zoom, _scroll.MinZoomFactor, _scroll.MaxZoomFactor);
        double z = CurrentZoom;
        var a = anchor ?? new Point(_scroll.ActualWidth / 2, _scroll.ActualHeight / 2);
        double cx = (CurrentH + a.X) / z, cy = (CurrentV + a.Y) / z;

        double h = Math.Clamp(cx * zoom - a.X, 0, Math.Max(0, _surface.Width * zoom - _scroll.ActualWidth));
        double v = Math.Clamp(cy * zoom - a.Y, 0, Math.Max(0, _surface.Height * zoom - _scroll.ActualHeight));
        _pending = true;
        (_targetZoom, _targetH, _targetV) = (zoom, h, v);
        _scroll.ChangeView(h, v, (float)zoom, !animate);
    }

    // ---------------------------------------------------------------- Maus

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!HasImage) return;
        var point = e.GetCurrentPoint(_scroll);
        if (point.Properties.IsHorizontalMouseWheel) return;
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;
        ZoomTo(CurrentZoom * Math.Pow(WheelStep, delta / 120.0), point.Position, animate: false);
        e.Handled = true; // sonst scrollt der ScrollViewer zusätzlich
    }


    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        // Touch verschiebt der ScrollViewer selbst
        if (!HasImage || e.Pointer.PointerDeviceType == PointerDeviceType.Touch || _scroll.ZoomFactor <= 1.001) return;
        var point = e.GetCurrentPoint(_scroll);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed) return;
        if (!_surface.CapturePointer(e.Pointer)) return;

        _panning = true;
        _panStart = point.Position;
        (_panH, _panV) = (_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _surface.SetCursor(InputSystemCursorShape.SizeAll);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetCurrentPoint(_scroll).Position;
        _pending = false;
        _scroll.ChangeView(_panH - (p.X - _panStart.X), _panV - (p.Y - _panStart.Y), null, true);
        e.Handled = true;
    }

    private void EndPan(Pointer? pointer)
    {
        if (!_panning) return;
        _panning = false;
        if (pointer is not null) _surface.ReleasePointerCapture(pointer);
        UpdateLabel();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (!HasImage) return;
        if (CurrentZoom > 1.01) Fit();
        else ZoomTo(Math.Max(OneToOne, 2), e.GetPosition(_scroll), animate: true);
        e.Handled = true;
    }

    /// <summary>Kurzer Hinweis für den Tooltip.</summary>
    public static string HelpText => Loc.T(
        "Mausrad: zoomen · Ziehen: verschieben · Doppelklick: Originalgröße / einpassen",
        "Mouse wheel: zoom · Drag: pan · Double-click: actual size / fit");
}
