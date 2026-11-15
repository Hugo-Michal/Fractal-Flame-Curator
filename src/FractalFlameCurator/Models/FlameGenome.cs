using System.Globalization;

namespace FractalFlameCurator.Models;

public sealed class FlameGenome
{
    public string Name { get; set; } = "flame";
    public long Seed { get; set; }
    public int Width { get; set; } = 2048;
    public int Height { get; set; } = 2048;
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Scale { get; set; } = 100;
    public double Rotate { get; set; }
    public int Oversample { get; set; } = 1;
    public double FilterRadius { get; set; } = 0.5;
    public long Quality { get; set; } = 20_000_000;
    public double Brightness { get; set; } = 1;
    public double Gamma { get; set; } = 2.2;
    public double GammaThreshold { get; set; } = 0.01;
    public double Vibrancy { get; set; } = 1;
    public double Hue { get; set; }
    public int Symmetry { get; set; }
    public PaletteDefinition Palette { get; set; } = PaletteDefinition.Monochrome;
    public List<FlameTransform> Transforms { get; } = [];
    public FlameTransform? FinalTransform { get; set; }

    public FlameGenome Clone()
    {
        var clone = new FlameGenome
        {
            Name = Name,
            Seed = Seed,
            Width = Width,
            Height = Height,
            CenterX = CenterX,
            CenterY = CenterY,
            Scale = Scale,
            Rotate = Rotate,
            Oversample = Oversample,
            FilterRadius = FilterRadius,
            Quality = Quality,
            Brightness = Brightness,
            Gamma = Gamma,
            GammaThreshold = GammaThreshold,
            Vibrancy = Vibrancy,
            Hue = Hue,
            Symmetry = Symmetry,
            Palette = Palette
        };
        clone.Transforms.AddRange(Transforms.Select(t => t.Clone()));
        clone.FinalTransform = FinalTransform?.Clone();
        return clone;
    }
}

public readonly record struct FlameCamera(double CenterX, double CenterY, double Scale, double Rotate)
{
    public static FlameCamera From(FlameGenome genome) => new(genome.CenterX, genome.CenterY, genome.Scale, genome.Rotate);

    public void ApplyTo(FlameGenome genome)
    {
        genome.CenterX = CenterX;
        genome.CenterY = CenterY;
        genome.Scale = Scale;
        genome.Rotate = Rotate;
    }

    public CameraPreviewTransform RelativePreviewFrom(FlameCamera source, double imageWidth, double imageHeight)
    {
        var scale = Scale / source.Scale;
        var angle = (Rotate - source.Rotate) * Math.PI / 180d;
        var cosine = Math.Cos(angle);
        var sine = Math.Sin(angle);
        var m11 = scale * cosine;
        var m12 = scale * sine;
        var m21 = -scale * sine;
        var m22 = scale * cosine;
        var translatedSourceX = source.CenterX * cosine - source.CenterY * sine;
        var translatedSourceY = source.CenterX * sine + source.CenterY * cosine;
        var translateX = imageWidth * Scale / 400d * (translatedSourceX - CenterX);
        var translateY = imageHeight * Scale / 400d * (translatedSourceY - CenterY);
        var centerX = imageWidth / 2d;
        var centerY = imageHeight / 2d;

        return new CameraPreviewTransform(
            m11,
            m12,
            m21,
            m22,
            centerX - m11 * centerX - m21 * centerY + translateX,
            centerY - m12 * centerX - m22 * centerY + translateY);
    }
}

public readonly record struct CameraPreviewTransform(double M11, double M12, double M21, double M22, double OffsetX, double OffsetY)
{
    public static CameraPreviewTransform Identity { get; } = new(1, 0, 0, 1, 0, 0);
}

public sealed class FlameTransform
{
    public double Weight { get; set; } = 1;
    public double Color { get; set; }
    public double Symmetry { get; set; } = 1;
    public double A { get; set; } = 1;
    public double B { get; set; }
    public double C { get; set; }
    public double D { get; set; } = 1;
    public double E { get; set; }
    public double F { get; set; }
    public AffineTransform? PostTransform { get; set; }
    public Dictionary<string, double> Variations { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FlameTransform Clone()
    {
        var clone = new FlameTransform
        {
            Weight = Weight,
            Color = Color,
            Symmetry = Symmetry,
            A = A,
            B = B,
            C = C,
            D = D,
            E = E,
            F = F,
            PostTransform = PostTransform
        };
        foreach (var pair in Variations)
        {
            clone.Variations[pair.Key] = pair.Value;
        }
        return clone;
    }
}

public sealed record AffineTransform(double A, double B, double C, double D, double E, double F);

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public string ToHex() => $"{R:X2}{G:X2}{B:X2}";
}

public sealed class PaletteDefinition
{
    public string Name { get; }
    public IReadOnlyList<RgbColor> Colors { get; }

    private PaletteDefinition(string name, IReadOnlyList<RgbColor> colors)
    {
        Name = name;
        Colors = colors;
    }

    public RgbColor Sample(double position)
    {
        var normalized = position - Math.Floor(position);
        var value = normalized * (Colors.Count - 1);
        var lower = Math.Clamp((int)value, 0, Colors.Count - 1);
        var upper = Math.Min(lower + 1, Colors.Count - 1);
        var t = value - lower;
        return new RgbColor(
            (byte)(Colors[lower].R + (Colors[upper].R - Colors[lower].R) * t),
            (byte)(Colors[lower].G + (Colors[upper].G - Colors[lower].G) * t),
            (byte)(Colors[lower].B + (Colors[upper].B - Colors[lower].B) * t));
    }

    public string ToHexString() => string.Concat(Colors.Select(c => c.ToHex()));

    public static PaletteDefinition FromHex(string name, string hex)
    {
        var colors = new List<RgbColor>();
        for (var i = 0; i + 5 < hex.Length; i += 6)
        {
            if (byte.TryParse(hex[i..(i + 2)], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
                byte.TryParse(hex[(i + 2)..(i + 4)], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
                byte.TryParse(hex[(i + 4)..(i + 6)], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                colors.Add(new RgbColor(r, g, b));
            }
        }
        return new PaletteDefinition(name, colors.Count == 0 ? Monochrome.Colors : colors);
    }

    public static PaletteDefinition Monochrome { get; } = CreateGradient("Monochrome", [new RgbColor(255, 255, 255), new RgbColor(0, 0, 0)]);
    public static PaletteDefinition Fire { get; } = CreateGradient("Fire", [new RgbColor(0, 0, 0), new RgbColor(100, 0, 0), new RgbColor(255, 75, 0), new RgbColor(255, 245, 160)]);
    public static PaletteDefinition Ocean { get; } = CreateGradient("Ocean", [new RgbColor(0, 5, 20), new RgbColor(0, 80, 140), new RgbColor(40, 220, 220), new RgbColor(230, 255, 255)]);
    public static PaletteDefinition Violet { get; } = CreateGradient("Violet", [new RgbColor(8, 0, 20), new RgbColor(65, 10, 135), new RgbColor(210, 55, 180), new RgbColor(255, 220, 255)]);

    public static IReadOnlyList<PaletteDefinition> BuiltIns { get; } = [Monochrome, Fire, Ocean, Violet];

    private static PaletteDefinition CreateGradient(string name, IReadOnlyList<RgbColor> stops)
    {
        var colors = new List<RgbColor>(256);
        for (var i = 0; i < 256; i++)
        {
            var position = i / 255d * (stops.Count - 1);
            var lower = Math.Min((int)position, stops.Count - 1);
            var upper = Math.Min(lower + 1, stops.Count - 1);
            var t = position - lower;
            colors.Add(new RgbColor(
                (byte)(stops[lower].R + (stops[upper].R - stops[lower].R) * t),
                (byte)(stops[lower].G + (stops[upper].G - stops[lower].G) * t),
                (byte)(stops[lower].B + (stops[upper].B - stops[lower].B) * t)));
        }
        return new PaletteDefinition(name, colors);
    }
}

public sealed record RenderSettings
{
    public int Width { get; init; } = 2048;
    public int Height { get; init; } = 2048;
    public long SampleBudget { get; init; } = 20_000_000;
    public int Oversample { get; init; } = 1;
    public int WorkerCount { get; init; } = 1;
    public bool NormalizeDensityBySampleCount { get; init; }
    public double FilterRadius { get; init; } = 0.5;
    public double Brightness { get; init; } = 1;
    public double Gamma { get; init; } = 2.2;
    public double Vibrancy { get; init; } = 1;
    public double WhitePoint { get; init; } = 0;
    public double BlackPoint { get; init; } = 1;
    public double ContrastCurve { get; init; } = 1;
    public double LowDensityCutoff { get; init; } = 0.01;
    public string PaletteName { get; init; } = PaletteDefinition.Monochrome.Name;
}

public sealed record RendererStatus(string Backend, string Device, bool IsGpu, string Detail);
