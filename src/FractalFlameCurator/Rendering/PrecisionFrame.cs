using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FractalFlameCurator.Rendering;

public sealed class HighPrecisionFrame(int width, int height, int channels, float[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Channels { get; } = channels;
    public float[] Pixels { get; } = pixels;
}

public sealed class RenderedFrame16(int width, int height, int channels, ushort[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Channels { get; } = channels;
    public ushort[] Pixels { get; } = pixels;

    public BitmapSource ToBitmapSource()
    {
        var format = Channels == 1 ? PixelFormats.Gray16 : PixelFormats.Rgb48;
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, format, null, Pixels, checked(Width * Channels * 2));
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

public static class PrecisionFrameProcessor
{
    public static HighPrecisionFrame Smooth(HighPrecisionFrame frame, double radius, int workers, CancellationToken cancellationToken)
    {
        if (!double.IsFinite(radius) || radius < 0 || radius > 3) throw new ArgumentOutOfRangeException(nameof(radius));
        cancellationToken.ThrowIfCancellationRequested();
        if (radius == 0) return frame;
        var extent = (int)Math.Ceiling(radius * 3);
        var weights = new double[extent * 2 + 1];
        var total = 0d;
        for (var i = -extent; i <= extent; i++)
        {
            weights[i + extent] = Math.Exp(-i * i / (2 * radius * radius));
            total += weights[i + extent];
        }
        for (var i = 0; i < weights.Length; i++) weights[i] /= total;
        var temporary = new float[frame.Pixels.Length];
        var output = new float[frame.Pixels.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(workers, 1, Math.Max(1, Environment.ProcessorCount)), CancellationToken = cancellationToken };
        Parallel.For(0, frame.Height, options, y =>
        {
            for (var x = 0; x < frame.Width; x++)
                for (var c = 0; c < frame.Channels; c++)
                {
                    var sum = 0d;
                    for (var k = -extent; k <= extent; k++)
                        sum += frame.Pixels[(y * frame.Width + Math.Clamp(x + k, 0, frame.Width - 1)) * frame.Channels + c] * weights[k + extent];
                    temporary[(y * frame.Width + x) * frame.Channels + c] = (float)sum;
                }
        });
        Parallel.For(0, frame.Height, options, y =>
        {
            for (var x = 0; x < frame.Width; x++)
                for (var c = 0; c < frame.Channels; c++)
                {
                    var sum = 0d;
                    for (var k = -extent; k <= extent; k++)
                        sum += temporary[(Math.Clamp(y + k, 0, frame.Height - 1) * frame.Width + x) * frame.Channels + c] * weights[k + extent];
                    output[(y * frame.Width + x) * frame.Channels + c] = (float)sum;
                }
        });
        return new HighPrecisionFrame(frame.Width, frame.Height, frame.Channels, output);
    }

    public static (RenderedFrame16 Frame, RgbHistogram Histogram) ApplyCurve(HighPrecisionFrame frame, ToneCurve curve, CancellationToken cancellationToken)
    {
        var lookup = curve.CreateValues(65536);
        var red = new long[256]; var green = new long[256]; var blue = new long[256];
        var output = new ushort[frame.Pixels.Length];
        for (var i = 0; i < frame.Pixels.Length; i++)
        {
            if ((i & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            var value = Math.Clamp(frame.Pixels[i], 0f, 1f);
            var bin = Math.Clamp((int)(value * 255), 0, 255);
            if (frame.Channels == 1) { red[bin]++; green[bin]++; blue[bin]++; }
            else if (i % 3 == 0) red[bin]++;
            else if (i % 3 == 1) green[bin]++;
            else blue[bin]++;
            var position = value * 65535d;
            var lower = (int)position;
            var upper = Math.Min(lower + 1, 65535);
            var mapped = lookup[lower] + (lookup[upper] - lookup[lower]) * (position - lower);
            output[i] = (ushort)Math.Clamp((int)Math.Round(mapped * 65535), 0, 65535);
        }
        return (new RenderedFrame16(frame.Width, frame.Height, frame.Channels, output), new RgbHistogram(red, green, blue));
    }
}
