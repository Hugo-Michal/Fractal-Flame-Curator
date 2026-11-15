using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using FractalFlameCurator.Rendering;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfPoint = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;

namespace FractalFlameCurator.Controls;

public sealed class ToneCurveEditor : FrameworkElement
{
    private const double MinimumSpacing = 1d / 1024;
    private ToneCurve _curve = ToneCurve.Identity;
    private RgbHistogram? _histogram;
    private int _selected;
    private bool _dragging;
    private bool _draggingInputMarker;

    public ToneCurveEditor()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = System.Windows.Input.Cursors.Cross;
        AutomationProperties.SetName(this, "Master RGB tone curve");
        AutomationProperties.SetHelpText(this, "Drag the black and white markers below the graph to set input levels. Click to add a curve point, drag to move it, right-click or press Delete to remove it. Arrow keys move the selected point; Insert adds one.");
    }

    public event EventHandler? CurveChanged;
    public ToneCurve Curve => _curve;
    public RgbHistogram? Histogram
    {
        get => _histogram;
        set { _histogram = value; InvalidateVisual(); }
    }

    public void Reset()
    {
        _selected = 0;
        SetCurve(ToneCurve.Identity);
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var plot = PlotBounds();
        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(12, 18, 25)), new Pen(new SolidColorBrush(Color.FromRgb(64, 86, 109)), 1), plot);
        if (plot.Width <= 0 || plot.Height <= 0) return;
        drawing.PushClip(new RectangleGeometry(plot));
        if (_histogram is not null) DrawHistogram(drawing, plot, _histogram);
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 145, 162, 180)), 0.7);
        for (var i = 1; i < 4; i++)
        {
            var x = plot.Left + plot.Width * i / 4;
            var y = plot.Top + plot.Height * i / 4;
            drawing.DrawLine(gridPen, new WpfPoint(x, plot.Top), new WpfPoint(x, plot.Bottom));
            drawing.DrawLine(gridPen, new WpfPoint(plot.Left, y), new WpfPoint(plot.Right, y));
        }
        var lookup = _curve.CreateLookup();
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToScreen(plot, 0, lookup[0] / 255d), false, false);
            for (var i = 1; i < lookup.Length; i++) context.LineTo(ToScreen(plot, i / 255d, lookup[i] / 255d), true, false);
        }
        geometry.Freeze();
        drawing.DrawGeometry(null, new Pen(Brushes.White, 2), geometry);
        drawing.Pop();
        for (var i = 0; i < _curve.Points.Count; i++)
        {
            var point = _curve.Points[i];
            drawing.DrawEllipse(i == _selected ? Brushes.LightSkyBlue : Brushes.White, new Pen(Brushes.Black, 1), ToScreen(plot, point.X, point.Y), 4.5, 4.5);
        }
        DrawInputMarker(drawing, plot, _curve.Points[0].X, true);
        DrawInputMarker(drawing, plot, _curve.Points[^1].X, false);
    }

    protected override void OnMouseLeftButtonDown(WpfMouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var plot = PlotBounds();
        var position = e.GetPosition(this);
        if (plot.Width <= 0 || plot.Height <= 0) return;
        var marker = FindInputMarker(plot, position);
        if (marker >= 0)
        {
            Focus();
            _selected = marker;
            _draggingInputMarker = true;
            _dragging = CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (!plot.Contains(position)) return;
        Focus();
        if (!SelectOrAddPoint(plot, position)) return;
        _draggingInputMarker = false;
        _dragging = CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    private bool SelectOrAddPoint(Rect plot, WpfPoint position)
    {
        var nearest = FindPoint(plot, position);
        if (nearest >= 0) _selected = nearest;
        else
        {
            var x = Math.Clamp((position.X - plot.Left) / plot.Width, 0, 1);
            var y = Math.Clamp((plot.Bottom - position.Y) / plot.Height, 0, 1);
            var points = _curve.Points.ToList();
            if (points.Count >= 256) return false;
            var index = points.FindIndex(p => p.X > x);
            if (index <= 0 || x - points[index - 1].X < MinimumSpacing || points[index].X - x < MinimumSpacing) return false;
            points.Insert(index, new ToneCurvePoint(x, y));
            _selected = index;
            SetCurve(new ToneCurve(points));
        }
        return true;
    }

    protected override void OnMouseMove(WpfMouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var plot = PlotBounds();
        if (plot.Width <= 0 || plot.Height <= 0) return;
        var position = e.GetPosition(this);
        if (_draggingInputMarker) MoveInputMarkerFromScreen(plot, position);
        else MoveSelectedFromScreen(plot, position);
        e.Handled = true;
    }

    private void MoveSelectedFromScreen(Rect plot, WpfPoint position) =>
        MoveSelected((position.X - plot.Left) / plot.Width, (plot.Bottom - position.Y) / plot.Height);

    private void MoveInputMarkerFromScreen(Rect plot, WpfPoint position) =>
        MoveSelected((position.X - plot.Left) / plot.Width, _curve.Points[_selected].Y);

    protected override void OnMouseLeftButtonUp(WpfMouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        _draggingInputMarker = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(WpfMouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        _draggingInputMarker = false;
    }

    protected override void OnMouseRightButtonDown(WpfMouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        var index = FindPoint(PlotBounds(), e.GetPosition(this));
        if (index <= 0 || index >= _curve.Points.Count - 1) return;
        _selected = index;
        RemoveSelected();
        e.Handled = true;
    }

    protected override void OnKeyDown(WpfKeyEventArgs e)
    {
        base.OnKeyDown(e);
        var point = _curve.Points[_selected];
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.05 : 0.01;
        switch (e.Key)
        {
            case Key.Left: MoveSelected(point.X - step, point.Y); break;
            case Key.Right: MoveSelected(point.X + step, point.Y); break;
            case Key.Up: MoveSelected(point.X, point.Y + step); break;
            case Key.Down: MoveSelected(point.X, point.Y - step); break;
            case Key.Delete: RemoveSelected(); break;
            case Key.Insert:
                var points = _curve.Points.ToList();
                if (points.Count >= 256) return;
                var index = Enumerable.Range(0, points.Count - 1).MaxBy(i => points[i + 1].X - points[i].X);
                if (points[index + 1].X - points[index].X < 2 * MinimumSpacing) return;
                var x = (points[index].X + points[index + 1].X) / 2;
                var y = _curve.CreateLookup()[(int)Math.Round(x * 255)] / 255d;
                points.Insert(index + 1, new ToneCurvePoint(x, y));
                _selected = index + 1;
                SetCurve(new ToneCurve(points));
                break;
            default: return;
        }
        e.Handled = true;
    }

    private void MoveSelected(double x, double y)
    {
        var points = _curve.Points.ToList();
        var minimum = _selected == 0 ? 0 : points[_selected - 1].X + MinimumSpacing;
        var maximum = _selected == points.Count - 1 ? 1 : points[_selected + 1].X - MinimumSpacing;
        x = Math.Clamp(x, minimum, maximum);
        var next = new ToneCurvePoint(x, Math.Clamp(y, 0, 1));
        if (points[_selected] == next) return;
        points[_selected] = next;
        SetCurve(new ToneCurve(points));
    }

    private void RemoveSelected()
    {
        var points = _curve.Points.ToList();
        if (_selected <= 0 || _selected >= points.Count - 1) return;
        points.RemoveAt(_selected);
        _selected = Math.Min(_selected, points.Count - 1);
        SetCurve(new ToneCurve(points));
    }

    private void SetCurve(ToneCurve curve)
    {
        if (_curve.Equals(curve)) { InvalidateVisual(); return; }
        _curve = curve;
        InvalidateVisual();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    private Rect PlotBounds() => new(8, 8, Math.Max(0, ActualWidth - 16), Math.Max(0, ActualHeight - 28));
    private static WpfPoint ToScreen(Rect plot, double x, double y) => new(plot.Left + x * plot.Width, plot.Bottom - y * plot.Height);

    private static void DrawInputMarker(DrawingContext drawing, Rect plot, double x, bool black)
    {
        var center = plot.Left + x * plot.Width;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new WpfPoint(center, plot.Bottom + 1), true, true);
            context.LineTo(new WpfPoint(center - 6, plot.Bottom + 13), true, false);
            context.LineTo(new WpfPoint(center + 6, plot.Bottom + 13), true, false);
        }
        geometry.Freeze();
        drawing.DrawGeometry(black ? Brushes.Black : Brushes.White, new Pen(black ? Brushes.White : Brushes.Black, 1), geometry);
    }

    private int FindInputMarker(Rect plot, WpfPoint mouse)
    {
        if (mouse.Y < plot.Bottom + 1 || mouse.Y > plot.Bottom + 16) return -1;
        var left = Math.Abs(mouse.X - (plot.Left + _curve.Points[0].X * plot.Width));
        var right = Math.Abs(mouse.X - (plot.Left + _curve.Points[^1].X * plot.Width));
        if (Math.Min(left, right) > 9) return -1;
        return left <= right ? 0 : _curve.Points.Count - 1;
    }
    private int FindPoint(Rect plot, WpfPoint mouse)
    {
        var best = -1;
        var distance = 100d;
        for (var i = 0; i < _curve.Points.Count; i++)
        {
            var point = _curve.Points[i];
            var screen = ToScreen(plot, point.X, point.Y);
            var d = (screen.X - mouse.X) * (screen.X - mouse.X) + (screen.Y - mouse.Y) * (screen.Y - mouse.Y);
            if (d < distance) { distance = d; best = i; }
        }
        return best;
    }

    private static void DrawHistogram(DrawingContext drawing, Rect plot, RgbHistogram histogram)
    {
        var max = Math.Max(histogram.Red.Max(), Math.Max(histogram.Green.Max(), histogram.Blue.Max()));
        if (max <= 0) return;
        var scale = Math.Log(1d + max);
        void Channel(long[] bins, Color color)
        {
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new WpfPoint(plot.Left, plot.Bottom), true, true);
                for (var i = 0; i < bins.Length; i++)
                    context.LineTo(new WpfPoint(plot.Left + plot.Width * i / 255d, plot.Bottom - plot.Height * Math.Log(1d + bins[i]) / scale), true, false);
                context.LineTo(new WpfPoint(plot.Right, plot.Bottom), true, false);
            }
            geometry.Freeze();
            drawing.DrawGeometry(new SolidColorBrush(color), null, geometry);
        }
        if (histogram.Red.SequenceEqual(histogram.Green) && histogram.Red.SequenceEqual(histogram.Blue))
            Channel(histogram.Red, Color.FromArgb(110, 210, 210, 210));
        else
        {
            Channel(histogram.Red, Color.FromArgb(80, 255, 70, 70));
            Channel(histogram.Green, Color.FromArgb(80, 70, 255, 120));
            Channel(histogram.Blue, Color.FromArgb(80, 90, 145, 255));
        }
    }
}
