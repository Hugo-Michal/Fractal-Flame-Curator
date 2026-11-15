using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FractalFlameCurator.Generation;
using FractalFlameCurator.Models;

namespace FractalFlameCurator.Serialization;

public static class FlameXmlSerializer
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string Serialize(FlameGenome genome)
    {
        FlameValidator.ThrowIfInvalid(genome);
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace };
        using var stringWriter = new StringWriter(Invariant);
        using (var writer = XmlWriter.Create(stringWriter, settings))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("flames");
            writer.WriteStartElement("flame");
            WriteAttribute(writer, "name", genome.Name);
            WriteAttribute(writer, "version", "Apophysis 7X");
            WriteAttribute(writer, "seed", genome.Seed);
            WriteAttribute(writer, "size", $"{genome.Width} {genome.Height}");
            WriteAttribute(writer, "center", $"{F(genome.CenterX)} {F(genome.CenterY)}");
            WriteAttribute(writer, "scale", genome.Scale);
            WriteAttribute(writer, "rotate", genome.Rotate);
            WriteAttribute(writer, "symmetry", genome.Symmetry);
            WriteAttribute(writer, "oversample", genome.Oversample);
            WriteAttribute(writer, "filter", genome.FilterRadius);
            WriteAttribute(writer, "quality", ToApophysisSampleDensity(genome));
            WriteAttribute(writer, "background", "1 1 1");
            WriteAttribute(writer, "brightness", genome.Brightness);
            WriteAttribute(writer, "gamma", genome.Gamma);
            WriteAttribute(writer, "gamma_threshold", genome.GammaThreshold);
            WriteAttribute(writer, "vibrancy", genome.Vibrancy);
            WriteAttribute(writer, "hue_rotation", genome.Hue);

            foreach (var transform in genome.Transforms)
            {
                WriteTransform(writer, "xform", transform);
            }
            if (genome.FinalTransform is not null)
            {
                WriteTransform(writer, "finalxform", genome.FinalTransform);
            }

            writer.WriteStartElement("palette");
            WriteAttribute(writer, "count", 256);
            WriteAttribute(writer, "format", "RGB");
            writer.WriteString(genome.Palette.ToHexString());
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }
        return stringWriter.ToString();
    }

    public static void Save(FlameGenome genome, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, Serialize(genome), Utf8NoBom);
    }

    public static FlameGenome Deserialize(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var flame = document.Root?.Element("flame") ?? throw new InvalidDataException("The .flame document has no flame element.");
        var size = ReadPair(flame.Attribute("size")?.Value, (2048, 2048));
        var center = ReadPair(flame.Attribute("center")?.Value, (0d, 0d));
        var width = (int)size.Item1;
        var height = (int)size.Item2;
        var sampleDensity = Double(flame, "quality", 5);
        var usesLegacyTransformAttributes = flame.Elements()
            .Where(element => element.Name.LocalName is "xform" or "finalxform")
            .Any(element => element.Attribute("coefs") is null
                            && (element.Attribute("a") is not null
                                || element.Attributes().Any(attribute => attribute.Name.LocalName.StartsWith("var_", StringComparison.OrdinalIgnoreCase))));
        var genome = new FlameGenome
        {
            Name = String(flame, "name", "flame"),
            Seed = Long(flame, "seed", 0),
            Width = width,
            Height = height,
            CenterX = center.Item1,
            CenterY = center.Item2,
            Scale = Double(flame, "scale", 100),
            Rotate = Double(flame, "rotate", 0),
            Symmetry = (int)Double(flame, "symmetry", 0),
            Oversample = (int)Double(flame, "oversample", 1),
            FilterRadius = Double(flame, "filter", 0.5),
            Quality = usesLegacyTransformAttributes ? ToLegacySampleBudget(sampleDensity) : ToSampleBudget(sampleDensity, width, height),
            Brightness = Double(flame, "brightness", 1),
            Gamma = Double(flame, "gamma", 2.2),
            GammaThreshold = Double(flame, "gamma_threshold", 0.01),
            Vibrancy = Double(flame, "vibrancy", 1),
            Hue = Double(flame, "hue_rotation", Double(flame, "hue", 0))
        };

        foreach (var element in flame.Elements().Where(e => e.Name.LocalName is "xform" or "finalxform"))
        {
            var transform = ReadTransform(element);
            if (element.Name.LocalName == "finalxform") genome.FinalTransform = transform;
            else genome.Transforms.Add(transform);
        }
        var palette = flame.Element("palette");
        if (palette is not null) genome.Palette = PaletteDefinition.FromHex("Imported", palette.Value.Trim());
        FlameValidator.ThrowIfInvalid(genome);
        return genome;
    }

    public static FlameGenome Load(string path) => Deserialize(File.ReadAllText(path));

    private static void WriteTransform(XmlWriter writer, string elementName, FlameTransform transform)
    {
        writer.WriteStartElement(elementName);
        WriteAttribute(writer, "weight", transform.Weight);
        WriteAttribute(writer, "color", transform.Color);
        WriteAttribute(writer, "symmetry", transform.Symmetry);
        WriteAttribute(writer, "coefs", $"{F(transform.A)} {F(transform.B)} {F(transform.C)} {F(transform.D)} {F(transform.E)} {F(transform.F)}");
        foreach (var pair in VariationRegistry.All.Select(d => d.Name).Where(transform.Variations.ContainsKey))
        {
            WriteAttribute(writer, pair, transform.Variations[pair]);
        }
        if (transform.PostTransform is { } post)
        {
            WriteAttribute(writer, "post", $"{F(post.A)} {F(post.B)} {F(post.C)} {F(post.D)} {F(post.E)} {F(post.F)}");
        }
        writer.WriteEndElement();
    }

    private static FlameTransform ReadTransform(XElement element)
    {
        var coefs = ReadSix(element.Attribute("coefs")?.Value);
        var transform = new FlameTransform
        {
            Weight = Double(element, "weight", 1),
            Color = Double(element, "color", 0),
            Symmetry = Double(element, "symmetry", 1),
            A = coefs?[0] ?? Double(element, "a", 1),
            B = coefs?[1] ?? Double(element, "b", 0),
            C = coefs?[2] ?? Double(element, "c", 0),
            D = coefs?[3] ?? Double(element, "d", 1),
            E = coefs?[4] ?? Double(element, "e", 0),
            F = coefs?[5] ?? Double(element, "f", 0)
        };
        foreach (var attribute in element.Attributes())
        {
            var name = attribute.Name.LocalName.StartsWith("var_", StringComparison.OrdinalIgnoreCase)
                ? attribute.Name.LocalName[4..]
                : attribute.Name.LocalName;
            if (VariationRegistry.Names.Contains(name)) transform.Variations[name] = ParseDouble(attribute.Value, 0);
        }
        var postParts = element.Attribute("post")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (postParts is { Length: 6 } && postParts.All(p => double.TryParse(p, NumberStyles.Float, Invariant, out _)))
        {
            transform.PostTransform = new AffineTransform(
                ParseDouble(postParts[0], 1), ParseDouble(postParts[1], 0), ParseDouble(postParts[2], 0),
                ParseDouble(postParts[3], 1), ParseDouble(postParts[4], 0), ParseDouble(postParts[5], 0));
        }
        return transform;
    }

    private static (double, double) ReadPair(string? value, (double, double) fallback)
    {
        var parts = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts is { Length: 2 } ? (ParseDouble(parts[0], fallback.Item1), ParseDouble(parts[1], fallback.Item2)) : fallback;
    }

    private static double[]? ReadSix(string? value)
    {
        var parts = value?.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts is { Length: 6 } && parts.All(part => double.TryParse(part, NumberStyles.Float, Invariant, out _))
            ? parts.Select(part => ParseDouble(part, 0)).ToArray()
            : null;
    }

    private static double ToApophysisSampleDensity(FlameGenome genome)
    {
        var pixelCount = (double)genome.Width * genome.Height;
        return Math.Max(0.1, genome.Quality / pixelCount);
    }

    private static long ToSampleBudget(double sampleDensity, int width, int height)
    {
        var totalSamples = sampleDensity * width * (double)height;
        return ToLongSampleBudget(totalSamples);
    }

    private static long ToLegacySampleBudget(double quality) => ToLongSampleBudget(quality);

    private static long ToLongSampleBudget(double value)
    {
        if (!double.IsFinite(value)) return 20_000_000;
        if (value >= long.MaxValue) return long.MaxValue;
        return (long)Math.Max(100, Math.Round(value));
    }

    private static string String(XElement element, string name, string fallback) => element.Attribute(name)?.Value ?? fallback;
    private static long Long(XElement element, string name, long fallback) => long.TryParse(element.Attribute(name)?.Value, NumberStyles.Integer, Invariant, out var result) ? result : fallback;
    private static double Double(XElement element, string name, double fallback) => ParseDouble(element.Attribute(name)?.Value, fallback);
    private static double ParseDouble(string? value, double fallback) => double.TryParse(value, NumberStyles.Float, Invariant, out var result) ? result : fallback;
    private static string F(double value) => value.ToString("R", Invariant);
    private static void WriteAttribute(XmlWriter writer, string name, object value) => writer.WriteAttributeString(name, Convert.ToString(value, Invariant));
}
