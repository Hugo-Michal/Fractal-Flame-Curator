using System.Windows.Media;
using System.Windows.Media.Imaging;
using FractalFlameCurator.Generation;
using FractalFlameCurator.Models;

namespace FractalFlameCurator.Rendering;

public sealed record RenderProgress(int CompletedSamples, int TotalSamples);

public sealed class RenderedFrame
{
    public RenderedFrame(int width, int height, byte[] bgraPixels)
    {
        Width = width;
        Height = height;
        BgraPixels = bgraPixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] BgraPixels { get; }

    public BitmapSource ToBitmapSource()
    {
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, BgraPixels, Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public void SavePng(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(ToBitmapSource()));
        encoder.Save(stream);
    }
}

public interface IFlameRenderer
{
    RendererStatus Status { get; }
    Task<RenderedFrame> RenderAsync(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken);
}

public sealed class CpuFlameRenderer : IFlameRenderer
{
    public RendererStatus Status { get; } = new(
        "CPU",
        $"Managed CPU ({Math.Max(1, Environment.ProcessorCount)} logical workers)",
        false,
        "Built-in renderer uses bounded managed CPU execution; no GPU backend is installed or claimed.");

    public Task<RenderedFrame> RenderAsync(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() => Render(genome, settings, progress, cancellationToken), cancellationToken);
    }

    private static RenderedFrame Render(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
    {
        FlameValidator.ThrowIfInvalid(genome);
        var width = Math.Clamp(settings.Width, 32, 4096);
        var height = Math.Clamp(settings.Height, 32, 4096);
        var oversample = Math.Clamp(settings.Oversample, 1, 3);
        var internalWidth = checked(width * oversample);
        var internalHeight = checked(height * oversample);
        var sampleBudget = Math.Max(100, settings.SampleBudget);
        var counts = new double[internalWidth * internalHeight];
        var reds = new double[counts.Length];
        var greens = new double[counts.Length];
        var blues = new double[counts.Length];
        var random = new DeterministicRandom(genome.Seed);
        var cumulativeWeights = BuildWeights(genome.Transforms);
        var x = random.NextSigned(1);
        var y = random.NextSigned(1);
        var colorPosition = 0.5;
        var burnIn = 40;

        for (var i = 0; i < sampleBudget + burnIn; i++)
        {
            if ((i & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new RenderProgress(Math.Min(i, sampleBudget), sampleBudget));
            }

            var transform = genome.Transforms[PickTransform(cumulativeWeights, random.NextDouble() * cumulativeWeights[^1])];
            if (!TryApplyTransform(transform, x, y, random, out var transformedX, out var transformedY))
            {
                x = random.NextSigned(1);
                y = random.NextSigned(1);
                continue;
            }
            x = transformedX;
            y = transformedY;
            ApplySymmetry(genome.Symmetry, random, ref x, ref y);
            colorPosition = colorPosition * 0.65 + transform.Color * 0.35;

            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1000 || Math.Abs(y) > 1000)
            {
                x = random.NextSigned(1);
                y = random.NextSigned(1);
                continue;
            }
            if (i < burnIn) continue;

            var plotX = x;
            var plotY = y;
            if (genome.FinalTransform is { } finalTransform
                && !TryApplyTransform(finalTransform, x, y, random, out plotX, out plotY)) continue;
            var cameraAngle = genome.Rotate * Math.PI / 180;
            var cameraX = plotX * Math.Cos(cameraAngle) - plotY * Math.Sin(cameraAngle);
            var cameraY = plotX * Math.Sin(cameraAngle) + plotY * Math.Cos(cameraAngle);
            var viewSpan = 4d / (genome.Scale / 100d);
            var px = (int)Math.Round((cameraX - genome.CenterX) / viewSpan * internalWidth + internalWidth / 2d);
            var py = (int)Math.Round((cameraY - genome.CenterY) / viewSpan * internalHeight + internalHeight / 2d);
            if ((uint)px >= (uint)internalWidth || (uint)py >= (uint)internalHeight) continue;
            var index = py * internalWidth + px;
            var paletteColor = genome.Palette.Sample(colorPosition);
            counts[index] += 1;
            reds[index] += paletteColor.R;
            greens[index] += paletteColor.G;
            blues[index] += paletteColor.B;
        }
        progress?.Report(new RenderProgress(sampleBudget, sampleBudget));
        cancellationToken.ThrowIfCancellationRequested();

        var internalPixels = ToneMapper.Map(counts, reds, greens, blues, settings, string.Equals(genome.Palette.Name, PaletteDefinition.Monochrome.Name, StringComparison.OrdinalIgnoreCase));
        return new RenderedFrame(width, height, Downsample(internalPixels, internalWidth, internalHeight, width, height, oversample, settings.FilterRadius));
    }

    private static double[] BuildWeights(IReadOnlyList<FlameTransform> transforms)
    {
        var weights = new double[transforms.Count];
        var total = 0d;
        for (var i = 0; i < transforms.Count; i++)
        {
            total += transforms[i].Weight;
            weights[i] = total;
        }
        return weights;
    }

    private static int PickTransform(double[] cumulativeWeights, double value)
    {
        var index = Array.BinarySearch(cumulativeWeights, value);
        if (index < 0) index = ~index;
        return Math.Clamp(index, 0, cumulativeWeights.Length - 1);
    }

    private static void ApplySymmetry(int symmetry, DeterministicRandom random, ref double x, ref double y)
    {
        if (symmetry is 0 or 1) return;

        var order = symmetry < -1 ? -symmetry : symmetry > 1 ? symmetry : 1;
        if (order > 1)
        {
            var angle = random.NextInt(0, order) * Math.Tau / order;
            (x, y) = (x * Math.Cos(angle) - y * Math.Sin(angle), x * Math.Sin(angle) + y * Math.Cos(angle));
        }

        if (symmetry < 0 && random.NextBool(0.5)) x = -x;
    }

    private static bool TryApplyTransform(FlameTransform transform, double x, double y, DeterministicRandom random, out double outputX, out double outputY)
    {
        var affineX = transform.A * x + transform.B * y + transform.E;
        var affineY = transform.C * x + transform.D * y + transform.F;
        outputX = 0;
        outputY = 0;
        var variationTotal = 0d;
        foreach (var variation in transform.Variations)
        {
            if (variation.Value <= 0) continue;
            var result = ApplyVariation(variation.Key, affineX, affineY, random);
            outputX += result.X * variation.Value;
            outputY += result.Y * variation.Value;
            variationTotal += variation.Value;
        }
        if (variationTotal <= 0 || !double.IsFinite(outputX) || !double.IsFinite(outputY)) return false;

        outputX /= variationTotal;
        outputY /= variationTotal;
        if (transform.PostTransform is { } post)
        {
            (outputX, outputY) = (post.A * outputX + post.B * outputY + post.E, post.C * outputX + post.D * outputY + post.F);
        }
        return double.IsFinite(outputX) && double.IsFinite(outputY);
    }

    private static byte[] Downsample(byte[] source, int sourceWidth, int sourceHeight, int width, int height, int oversample, double filterRadius)
    {
        if (oversample == 1 && filterRadius <= 0.75) return source;
        var output = new byte[width * height * 4];
        var radius = Math.Clamp((int)Math.Ceiling(filterRadius), 0, 3);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var centerX = x * oversample + (oversample - 1) / 2;
                var centerY = y * oversample + (oversample - 1) / 2;
                var sumB = 0d; var sumG = 0d; var sumR = 0d; var sumA = 0d; var total = 0d;
                for (var sy = centerY - radius; sy <= centerY + radius; sy++)
                {
                    if ((uint)sy >= (uint)sourceHeight) continue;
                    for (var sx = centerX - radius; sx <= centerX + radius; sx++)
                    {
                        if ((uint)sx >= (uint)sourceWidth) continue;
                        var distance = Math.Sqrt((sx - centerX) * (sx - centerX) + (sy - centerY) * (sy - centerY));
                        var weight = Math.Max(0.01, filterRadius + 1 - distance);
                        var index = (sy * sourceWidth + sx) * 4;
                        sumB += source[index] * weight;
                        sumG += source[index + 1] * weight;
                        sumR += source[index + 2] * weight;
                        sumA += source[index + 3] * weight;
                        total += weight;
                    }
                }
                var outputIndex = (y * width + x) * 4;
                output[outputIndex] = (byte)Math.Clamp(sumB / total, 0, 255);
                output[outputIndex + 1] = (byte)Math.Clamp(sumG / total, 0, 255);
                output[outputIndex + 2] = (byte)Math.Clamp(sumR / total, 0, 255);
                output[outputIndex + 3] = (byte)Math.Clamp(sumA / total, 0, 255);
            }
        }
        return output;
    }

    private static (double X, double Y) ApplyVariation(string name, double x, double y, DeterministicRandom random)
    {
        var r2 = x * x + y * y;
        var r = Math.Sqrt(r2) + 1e-12;
        var theta = Math.Atan2(y, x);
        return name.ToLowerInvariant() switch
        {
            "linear" => (x, y),
            "sinusoidal" => (Math.Sin(x), Math.Sin(y)),
            "spherical" => (x / r2, y / r2),
            "swirl" => (x * Math.Sin(r2) - y * Math.Cos(r2), x * Math.Cos(r2) + y * Math.Sin(r2)),
            "horseshoe" => ((x - y) * (x + y) / r, 2 * x * y / r),
            "polar" => (theta / Math.PI, r - 1),
            "handkerchief" => (r * Math.Sin(theta + r), r * Math.Cos(theta - r)),
            "heart" => (r * Math.Sin(theta * r), -r * Math.Cos(theta * r)),
            "disc" => (theta / Math.PI * Math.Sin(Math.PI * r), theta / Math.PI * Math.Cos(Math.PI * r)),
            "spiral" => ((Math.Cos(theta) + Math.Sin(r)) / r, (Math.Cos(theta) - Math.Sin(r)) / r),
            "hyperbolic" => (Math.Sin(theta) / r, r * Math.Cos(theta)),
            "diamond" => (Math.Sin(theta) * Math.Cos(r), Math.Cos(theta) * Math.Sin(r)),
            "ex" => (r * (Math.Sin(theta + r) * Math.Sin(theta - r) + r), r * (Math.Cos(theta + r) * Math.Cos(theta - r) - r)),
            "julia" => Julia(x, y, r, theta, random),
            "bent" => (x < 0 ? 2 * x : x, y < 0 ? 0.5 * y : y),
            "waves" => (x + 0.35 * Math.Sin(y / 0.7), y + 0.35 * Math.Sin(x / 0.7)),
            "fisheye" => (2 * y / (r + 1), 2 * x / (r + 1)),
            "popcorn" => (x + 0.05 * Math.Sin(Math.Tan(3 * y)), y + 0.05 * Math.Sin(Math.Tan(3 * x))),
            "exponential" => (Math.Exp(x - 1) * Math.Cos(Math.PI * y), Math.Exp(x - 1) * Math.Sin(Math.PI * y)),
            "power" => (Math.Pow(r, Math.Sin(theta)) * Math.Cos(theta), Math.Pow(r, Math.Sin(theta)) * Math.Sin(theta)),
            "cosine" => (Math.Cos(Math.PI * x) * Math.Cosh(y), -Math.Sin(Math.PI * x) * Math.Sinh(y)),
            "rings" => (x * (r + 0.25 - Math.Floor((r + 0.25) / 0.5) * 0.5), y * (r + 0.25 - Math.Floor((r + 0.25) / 0.5) * 0.5)),
            "fan" => Fan(x, y, theta),
            "blob" => (r * (0.8 + 0.25 * Math.Sin(3 * theta)) * Math.Cos(theta), r * (0.8 + 0.25 * Math.Sin(3 * theta)) * Math.Sin(theta)),
            "pdj" => (Math.Sin(2.2 * y) - Math.Cos(2.2 * x), Math.Sin(2.2 * x) - Math.Cos(2.2 * y)),
            "perspective" => (x / (1.3 - y * 0.3), y / (1.3 - y * 0.3)),
            "noise" => (x * (0.8 + random.NextDouble() * 0.4), y * (0.8 + random.NextDouble() * 0.4)),
            "julian" => Julia(x, y, r, theta * 0.5, random),
            "juliascope" => Julia(x, y, r, Math.Abs(theta), random),
            "curl" => Curl(x, y),
            "rectangles" => (Math.Round(x) + (x - Math.Round(x)) * 0.4, Math.Round(y) + (y - Math.Round(y)) * 0.4),
            "tangent" => (Math.Sin(x) / Math.Cos(y), Math.Tan(y)),
            "cross" => (x / Math.Sqrt((x * x - y * y) * (x * x - y * y) + 1e-6), y / Math.Sqrt((x * x - y * y) * (x * x - y * y) + 1e-6)),
            "rays" => (Math.Tan(theta) * Math.Sin(r) / r, Math.Tan(theta) * Math.Cos(r) / r),
            "secant" => (x, 1 / Math.Cos(y)),
            "twintrian" => (Math.Sin(theta) * Math.Sin(r), Math.Sin(theta) * Math.Cos(r)),
            "blur" => (r * Math.Cos(random.NextDouble() * Math.Tau), r * Math.Sin(random.NextDouble() * Math.Tau)),
            "radial_blur" => (r * Math.Cos(theta + random.NextSigned(0.4)), r * Math.Sin(theta + random.NextSigned(0.4))),
            _ => (x, y)
        };
    }

    private static (double X, double Y) Julia(double x, double y, double r, double theta, DeterministicRandom random)
    {
        var angle = theta * 0.5 + (random.NextBool() ? 0 : Math.PI);
        var root = Math.Sqrt(r);
        return (root * Math.Cos(angle), root * Math.Sin(angle));
    }

    private static (double X, double Y) Fan(double x, double y, double theta)
    {
        var halfPi = Math.PI * 0.5;
        var t = theta + Math.Floor(theta / Math.PI + 0.5) * Math.PI;
        var angle = Math.Abs(t) > halfPi ? theta - halfPi : theta + halfPi;
        return (x * Math.Cos(angle) - y * Math.Sin(angle), x * Math.Sin(angle) + y * Math.Cos(angle));
    }

    private static (double X, double Y) Curl(double x, double y)
    {
        const double c1 = 0.7;
        const double c2 = 0.2;
        var denominator = (c1 * x + c2 * y) * (c1 * x + c2 * y) + 1;
        return ((x * c1 + y * c2) / denominator, (x * c2 - y * c1) / denominator);
    }

}
