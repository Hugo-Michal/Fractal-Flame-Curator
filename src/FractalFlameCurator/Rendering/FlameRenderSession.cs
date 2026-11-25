using FractalFlameCurator.Models;

namespace FractalFlameCurator.Rendering;

public enum FlameRenderChange { None, Adjustment, Sampling }

public sealed class PreparedFlameFrame
{
    internal PreparedFlameFrame(RenderedFrame16 frame, FlameHistogram histogram, RgbHistogram colorHistogram, RenderSettings settings, double smoothing, ToneCurve curve, bool automaticCurve)
    {
        Frame = frame;
        Histogram = histogram;
        ColorHistogram = colorHistogram;
        Settings = settings;
        Smoothing = smoothing;
        Curve = curve;
        AutomaticCurve = automaticCurve;
    }

    public RenderedFrame16 Frame { get; }
    public RgbHistogram ColorHistogram { get; }
    internal FlameHistogram Histogram { get; }
    internal RenderSettings Settings { get; }
    internal double Smoothing { get; }
    internal ToneCurve Curve { get; }
    internal bool AutomaticCurve { get; }
}

public sealed class FlameRenderSession(IHistogramFlameRenderer renderer)
{
    private FlameHistogram? _histogram;
    private SamplingKey? _sampling;
    private AdjustmentKey? _adjustment;
    private RenderedFrame16? _frame;
    private RgbHistogram? _colorHistogram;
    private ToneCurve? _effectiveCurve;

    public FlameRenderChange GetChange(FlameGenome genome, RenderSettings settings, double smoothing, ToneCurve? curve = null) =>
        GetChange(genome, settings, smoothing, curve, false);

    public FlameRenderChange GetChange(FlameGenome genome, RenderSettings settings, double smoothing, ToneCurve? curve, bool automaticCurve)
    {
        if (_histogram is null || _sampling != SamplingKey.From(genome, settings)) return FlameRenderChange.Sampling;
        return _adjustment == AdjustmentKey.From(settings, smoothing, curve ?? ToneCurve.Identity, automaticCurve) ? FlameRenderChange.None : FlameRenderChange.Adjustment;
    }

    public bool IsInputCurrent(FlameGenome genome, RenderSettings settings, double smoothing) =>
        _histogram is not null && _sampling == SamplingKey.From(genome, settings) && _adjustment is not null &&
        _adjustment == AdjustmentKey.From(settings, smoothing, _adjustment.Curve, _adjustment.AutomaticCurve);

    public Task<PreparedFlameFrame> PrepareAsync(FlameGenome genome, RenderSettings settings, double smoothing,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken, ToneCurve? curve = null) =>
        PrepareAsync(genome, settings, smoothing, progress, cancellationToken, curve, false);

    public async Task<PreparedFlameFrame> PrepareAsync(FlameGenome genome, RenderSettings settings, double smoothing,
        IProgress<RenderProgress>? progress, CancellationToken cancellationToken, ToneCurve? curve, bool automaticCurve)
    {
        cancellationToken.ThrowIfCancellationRequested();
        curve ??= ToneCurve.Identity;
        var change = GetChange(genome, settings, smoothing, curve, automaticCurve);
        if (change == FlameRenderChange.None) return new PreparedFlameFrame(_frame!, _histogram!, _colorHistogram!, settings, smoothing, _effectiveCurve!, automaticCurve);

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
            _effectiveCurve = null;
        }
        if (histogram is null) throw new InvalidOperationException("Sample the flame before applying adjustments.");
        var frame = await renderer.MapHighPrecisionAsync(histogram, settings, cancellationToken);
        if (smoothing > 0)
            frame = await Task.Run(() => PrecisionFrameProcessor.Smooth(frame, smoothing, settings.WorkerCount, cancellationToken), cancellationToken);
        if (automaticCurve)
            curve = await Task.Run(() => PrecisionFrameProcessor.CreateAutomaticCurve(frame, cancellationToken), cancellationToken);
        var processed = await Task.Run(() => PrecisionFrameProcessor.ApplyCurve(frame, curve, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparedFlameFrame(processed.Frame, histogram, processed.Histogram, settings, smoothing, curve, automaticCurve);
    }

    public void Commit(PreparedFlameFrame prepared)
    {
        if (!ReferenceEquals(_histogram, prepared.Histogram)) throw new InvalidOperationException("The sampled flame has changed.");
        _adjustment = AdjustmentKey.From(prepared.Settings, prepared.Smoothing, prepared.Curve, prepared.AutomaticCurve);
        _frame = prepared.Frame;
        _colorHistogram = prepared.ColorHistogram;
        _effectiveCurve = prepared.Curve;
    }

    public void Clear()
    {
        _histogram = null;
        _sampling = null;
        _adjustment = null;
        _frame = null;
        _colorHistogram = null;
        _effectiveCurve = null;
    }

    private sealed record SamplingKey(long Seed, int Symmetry, FlameCamera Camera, int Width, int Height, long Samples, int Oversample,
        int Workers, PaletteDefinition Palette)
    {
        public static SamplingKey From(FlameGenome genome, RenderSettings settings) =>
            new(genome.Seed, genome.Symmetry, FlameCamera.From(genome), settings.Width, settings.Height, settings.SampleBudget,
                settings.Oversample, settings.WorkerCount, genome.Palette);
    }

    private sealed record AdjustmentKey(double Filter, double Gamma, double Brightness, double Vibrancy,
        double White, double Black, double Contrast, double Cutoff, bool StableTone, double Smoothing, ToneCurve Curve, bool AutomaticCurve)
    {
        public static AdjustmentKey From(RenderSettings settings, double smoothing, ToneCurve curve, bool automaticCurve) =>
            new(settings.FilterRadius, settings.Gamma, settings.Brightness, settings.Vibrancy,
                settings.WhitePoint, settings.BlackPoint, settings.ContrastCurve, settings.LowDensityCutoff,
                settings.NormalizeDensityBySampleCount, smoothing, automaticCurve ? ToneCurve.Identity : curve, automaticCurve);
    }
}
