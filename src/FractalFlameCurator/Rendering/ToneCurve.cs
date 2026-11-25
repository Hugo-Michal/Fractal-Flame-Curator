namespace FractalFlameCurator.Rendering;

public readonly record struct ToneCurvePoint(double X, double Y);

public sealed class ToneCurve : IEquatable<ToneCurve>
{
    private readonly ToneCurvePoint[] _points;
    private readonly IReadOnlyList<ToneCurvePoint> _view;

    public static ToneCurve Identity { get; } = new([new(0, 0), new(1, 1)]);
    public IReadOnlyList<ToneCurvePoint> Points => _view;

    public ToneCurve(IEnumerable<ToneCurvePoint> points)
    {
        _points = points.ToArray();
        if (_points.Length < 2 ||
            _points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1))
            throw new ArgumentException("Curve points must be finite, within zero to one, and include at least two points.", nameof(points));
        for (var i = 1; i < _points.Length; i++)
            if (_points[i].X <= _points[i - 1].X) throw new ArgumentException("Curve inputs must be strictly increasing.", nameof(points));
        _view = Array.AsReadOnly(_points);
    }

    public byte[] CreateLookup()
    {
        return CreateValues(256).Select(value => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255)).ToArray();
    }

    internal double[] CreateValues(int count)
    {
        var slopes = new double[_points.Length - 1];
        var tangents = new double[_points.Length];
        for (var i = 0; i < slopes.Length; i++)
            slopes[i] = (_points[i + 1].Y - _points[i].Y) / (_points[i + 1].X - _points[i].X);
        tangents[0] = slopes[0];
        tangents[^1] = slopes[^1];
        for (var i = 1; i < tangents.Length - 1; i++)
        {
            if (slopes[i - 1] * slopes[i] <= 0) continue;
            var left = _points[i].X - _points[i - 1].X;
            var right = _points[i + 1].X - _points[i].X;
            var w1 = 2 * right + left;
            var w2 = right + 2 * left;
            tangents[i] = (w1 + w2) / (w1 / slopes[i - 1] + w2 / slopes[i]);
        }

        var lookup = new double[count];
        var segment = 0;
        for (var value = 0; value < lookup.Length; value++)
        {
            var x = value / (double)(count - 1);
            if (x <= _points[0].X)
            {
                lookup[value] = _points[0].Y;
                continue;
            }
            if (x >= _points[^1].X)
            {
                lookup[value] = _points[^1].Y;
                continue;
            }
            while (segment < slopes.Length - 1 && x > _points[segment + 1].X) segment++;
            var a = _points[segment];
            var b = _points[segment + 1];
            var h = b.X - a.X;
            var t = (x - a.X) / h;
            var t2 = t * t;
            var t3 = t2 * t;
            var y = (2 * t3 - 3 * t2 + 1) * a.Y + (t3 - 2 * t2 + t) * h * tangents[segment]
                    + (-2 * t3 + 3 * t2) * b.Y + (t3 - t2) * h * tangents[segment + 1];
            y = Math.Clamp(y, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y));
            lookup[value] = y;
        }
        return lookup;
    }

    public bool Equals(ToneCurve? other) => other is not null && _points.SequenceEqual(other._points);
    public override bool Equals(object? obj) => obj is ToneCurve other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var point in _points) hash.Add(point);
        return hash.ToHashCode();
    }
}

public sealed record RgbHistogram(long[] Red, long[] Green, long[] Blue)
{
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
}

public static class BitmapToneCurve
{
    public static RgbHistogram Apply(RenderedFrame frame, ToneCurve curve, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lookup = curve.CreateLookup();
        var red = new long[256];
        var green = new long[256];
        var blue = new long[256];
        var pixels = frame.BgraPixels;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 262143) == 0) cancellationToken.ThrowIfCancellationRequested();
            blue[pixels[offset]]++;
            green[pixels[offset + 1]]++;
            red[pixels[offset + 2]]++;
            pixels[offset] = lookup[pixels[offset]];
            pixels[offset + 1] = lookup[pixels[offset + 1]];
            pixels[offset + 2] = lookup[pixels[offset + 2]];
        }
        return new RgbHistogram(red, green, blue);
    }
}
