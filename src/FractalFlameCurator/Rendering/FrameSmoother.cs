namespace FractalFlameCurator.Rendering;

public static class FrameSmoother
{
    public static RenderedFrame Apply(RenderedFrame frame, double radius, int workers, CancellationToken cancellationToken)
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

        var width = frame.Width;
        var height = frame.Height;
        var temporary = new byte[frame.BgraPixels.Length];
        var output = new byte[temporary.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(workers, 1, Math.Max(1, Environment.ProcessorCount)), CancellationToken = cancellationToken };
        Parallel.For(0, height, options, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var b = 0d; var g = 0d; var r = 0d;
                for (var k = -extent; k <= extent; k++)
                {
                    var source = (y * width + Math.Clamp(x + k, 0, width - 1)) * 4;
                    var weight = weights[k + extent];
                    b += frame.BgraPixels[source] * weight;
                    g += frame.BgraPixels[source + 1] * weight;
                    r += frame.BgraPixels[source + 2] * weight;
                }
                var target = (y * width + x) * 4;
                temporary[target] = (byte)Math.Clamp(Math.Round(b), 0, 255);
                temporary[target + 1] = (byte)Math.Clamp(Math.Round(g), 0, 255);
                temporary[target + 2] = (byte)Math.Clamp(Math.Round(r), 0, 255);
                temporary[target + 3] = frame.BgraPixels[target + 3];
            }
        });
        Parallel.For(0, height, options, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var b = 0d; var g = 0d; var r = 0d;
                for (var k = -extent; k <= extent; k++)
                {
                    var source = (Math.Clamp(y + k, 0, height - 1) * width + x) * 4;
                    var weight = weights[k + extent];
                    b += temporary[source] * weight;
                    g += temporary[source + 1] * weight;
                    r += temporary[source + 2] * weight;
                }
                var target = (y * width + x) * 4;
                output[target] = (byte)Math.Clamp(Math.Round(b), 0, 255);
                output[target + 1] = (byte)Math.Clamp(Math.Round(g), 0, 255);
                output[target + 2] = (byte)Math.Clamp(Math.Round(r), 0, 255);
                output[target + 3] = temporary[target + 3];
            }
        });
        return new RenderedFrame(width, height, output);
    }
}
