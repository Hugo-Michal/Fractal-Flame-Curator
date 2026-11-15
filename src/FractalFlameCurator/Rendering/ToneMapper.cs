using FractalFlameCurator.Models;

namespace FractalFlameCurator.Rendering;

public static class ToneMapper
{
    public static byte[] Map(double[] counts, double[] reds, double[] greens, double[] blues, RenderSettings settings, bool monochrome, CancellationToken cancellationToken = default)
    {
        if (counts.Length != reds.Length || counts.Length != greens.Length || counts.Length != blues.Length)
        {
            throw new ArgumentException("Tone-map channels must have the same length.");
        }

        var densityScale = settings.NormalizeDensityBySampleCount ? 20_000_000d / Math.Max(100, settings.SampleBudget) : 1d;
        var maxLog = 0d;
        for (var i = 0; i < counts.Length; i++)
        {
            if ((i & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            maxLog = Math.Max(maxLog, LogOnePlus(counts[i] * densityScale));
        }

        var pixels = new byte[counts.Length * 4];
        var gamma = Math.Clamp(settings.Gamma, 0.1, 8);
        var brightness = Math.Clamp(settings.Brightness, 0.05, 5);
        var vibrancy = Math.Clamp(settings.Vibrancy, 0, 1);
        var whitePoint = Math.Clamp(settings.WhitePoint, 0, 1);
        var blackPoint = Math.Clamp(settings.BlackPoint, 0, 1);
        if (blackPoint <= whitePoint) blackPoint = Math.Min(1, whitePoint + 0.000001);
        var contrastCurve = Math.Clamp(settings.ContrastCurve, 0.1, 8);
        var lowDensityCutoff = Math.Clamp(settings.LowDensityCutoff, 0, 1);

        for (var i = 0; i < counts.Length; i++)
        {
            if ((i & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            var rawDensity = maxLog <= 0 ? 0 : LogOnePlus(counts[i] * densityScale) / maxLog;
            var density = rawDensity < lowDensityCutoff
                ? 0
                : Math.Clamp((rawDensity - whitePoint) / Math.Max(0.000001, blackPoint - whitePoint), 0, 1);
            density = ApplyContrastCurve(density, contrastCurve);
            var tone = Math.Clamp(Math.Pow(Math.Clamp(density * brightness, 0, 1), 1 / gamma), 0, 1);
            var offset = i * 4;
            if (monochrome)
            {
                var ink = (byte)Math.Clamp(255 * (1 - tone), 0, 255);
                pixels[offset] = ink;
                pixels[offset + 1] = ink;
                pixels[offset + 2] = ink;
            }
            else
            {
                var averageR = counts[i] == 0 ? 0 : reds[i] / counts[i];
                var averageG = counts[i] == 0 ? 0 : greens[i] / counts[i];
                var averageB = counts[i] == 0 ? 0 : blues[i] / counts[i];
                var luminance = averageR * 0.2126 + averageG * 0.7152 + averageB * 0.0722;
                pixels[offset] = (byte)Math.Clamp(255 - tone * (255 - (averageB * vibrancy + luminance * (1 - vibrancy))), 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp(255 - tone * (255 - (averageG * vibrancy + luminance * (1 - vibrancy))), 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(255 - tone * (255 - (averageR * vibrancy + luminance * (1 - vibrancy))), 0, 255);
            }
            pixels[offset + 3] = 255;
        }

        return pixels;
    }

    private static double ApplyContrastCurve(double value, double contrastCurve)
    {
        if (Math.Abs(contrastCurve - 1) < 0.000001) return value;
        var centered = value * 2 - 1;
        return Math.Clamp(0.5 + Math.Sign(centered) * Math.Pow(Math.Abs(centered), 1 / contrastCurve) * 0.5, 0, 1);
    }

    private static double LogOnePlus(double value) => Math.Log(1 + value);
}
