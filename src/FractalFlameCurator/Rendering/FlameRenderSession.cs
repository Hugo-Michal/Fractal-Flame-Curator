using FractalFlameCurator.Models;

namespace FractalFlameCurator.Rendering;

public enum FlameRenderChange { None, Adjustment, Sampling }

public sealed class PreparedFlameFrame
{
    internal PreparedFlameFrame(RenderedFrame frame, FlameHistogram histogram, RgbHistogram colorHistogram, RenderSettings settings, double smoothing, ToneCurve curve)
    {
        Frame = frame;
        Histogram = histogram;
        ColorHistogram = colorHistogram;
        Settings = settings;
        Smoothing = smoothing;
        Curve = curve;
    }

    public RenderedFrame Frame { get; }
    public RgbHistogram ColorHistogram { get; }
    internal FlameHistogram Histogram { get; }
    internal RenderSettings Settings { get; }
    internal double Smoothing { get; }
    internal ToneCurve Curve { get; }
}

public sealed class FlameRenderSession(IHistogramFlameRenderer renderer)
{
    private FlameHistogram? _histogram;
    private SamplingKey? _sampling;
    private AdjustmentKey? _adjustment;
    private RenderedFrame? _frame;
    private RgbHistogram? _colorHistogram;

    public FlameRenderChange GetChange(FlameGenome genome, RenderSettings settings, double smoothing, ToneCurve? curve = null)
    {
        if (_histogram is null || _sampling != SamplingKey.From(genome, settings)) return FlameRenderChange.Sampling;
        return _adjustment == AdjustmentKey.From(settings, smoothing, curve ?? ToneCurve.Identity) ? FlameRenderChange.None : FlameRenderChange.Adjustment;
    }

    public async Task<PreparedFlameFrame> PrepareAsync(FlameGenome genome, RenderSettings settings, double smoothing,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken, ToneCurve? curve = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        curve ??= ToneCurve.Identity;
        var change = GetChange(genome, settings, smoothing, curve);
        if (change == FlameRenderChange.None) return new PreparedFlameFrame(_frame!, _histogram!, _colorHistogram!, settings, smoothing, curve);

        var histogram = _histogram;
        if (change == FlameRenderChange.Sampling)
        {
            histogram = await renderer.SampleAsync(genome, settings, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _histogram = histogram;
            _sampling = SamplingKey.From(genome, settings);
            _adjustment = null;
            _frame = null;
            _colorHistogram = null;
        }
        if (histogram is null) throw new InvalidOperationException("Sample the flame before applying adjustments.");
        var frame = await renderer.MapAsync(histogram, settings, cancellationToken);
        if (smoothing > 0)
            frame = await Task.Run(() => FrameSmoother.Apply(frame, smoothing, settings.WorkerCount, cancellationToken), cancellationToken);
        var colorHistogram = await Task.Run(() => BitmapToneCurve.Apply(frame, curve, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedFlameFrame(frame, histogram, colorHistogram, settings, smoothing, curve);
    }

    public void Commit(PreparedFlameFrame prepared)
    {
        if (!ReferenceEquals(_histogram, prepared.Histogram)) throw new InvalidOperationException("The sampled flame has changed.");
        _adjustment = AdjustmentKey.From(prepared.Settings, prepared.Smoothing, prepared.Curve);
        _frame = prepared.Frame;
        _colorHistogram = prepared.ColorHistogram;
    }

    public void Clear()
    {
        _histogram = null;
        _sampling = null;
        _adjustment = null;
        _frame = null;
        _colorHistogram = null;
    }

    private sealed record SamplingKey(long Seed, int Symmetry, FlameCamera Camera, int Width, int Height, long Samples, int Oversample,
        int Workers, PaletteDefinition Palette)
    {
        public static SamplingKey From(FlameGenome genome, RenderSettings settings) =>
            new(genome.Seed, genome.Symmetry, FlameCamera.From(genome), settings.Width, settings.Height, settings.SampleBudget,
                settings.Oversample, settings.WorkerCount, genome.Palette);
    }

    private sealed record AdjustmentKey(double Filter, double Gamma, double Brightness, double Vibrancy,
        double White, double Black, double Contrast, double Cutoff, bool StableTone, double Smoothing, ToneCurve Curve)
    {
        public static AdjustmentKey From(RenderSettings settings, double smoothing, ToneCurve curve) =>
            new(settings.FilterRadius, settings.Gamma, settings.Brightness, settings.Vibrancy,
                settings.WhitePoint, settings.BlackPoint, settings.ContrastCurve, settings.LowDensityCutoff,
                settings.NormalizeDensityBySampleCount, smoothing, curve);
    }
}
