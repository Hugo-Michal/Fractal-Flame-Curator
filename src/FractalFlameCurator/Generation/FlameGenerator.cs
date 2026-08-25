using FractalFlameCurator.Models;

namespace FractalFlameCurator.Generation;

public sealed record FlameGeneratorOptions
{
    public int Width { get; init; } = 2048;
    public int Height { get; init; } = 2048;
    public PaletteDefinition Palette { get; init; } = PaletteDefinition.Monochrome;
    public RandomGeneratorSettings GeneratorSettings { get; init; } = RandomGeneratorSettings.CreateDefault();
}

public sealed class FlameGenerator
{
    public FlameGenome Generate(
        long seed,
        FlameGeneratorOptions? options = null,
        SpaceFillingSimplexSampler? variationWeightSampler = null)
    {
        options ??= new FlameGeneratorOptions();
        var settings = options.GeneratorSettings.Snapshot();
        var random = new DeterministicRandom(seed);
        variationWeightSampler ??= new SpaceFillingSimplexSampler(seed);
        var genome = new FlameGenome
        {
            Name = $"flame_seed_{seed}",
            Seed = seed,
            Width = options.Width,
            Height = options.Height,
            CenterX = 0,
            CenterY = 0,
            Scale = 100,
            Rotate = 0,
            Oversample = 1,
            FilterRadius = 0.5,
            Quality = 20_000_000,
            Brightness = 1,
            Gamma = 1,
            Vibrancy = 1,
            Symmetry = GenerateSymmetry(settings.SymmetryTypes, random),
            Palette = options.Palette
        };

        var transformCount = random.NextInt(settings.MinimumTransformCount, settings.MaximumTransformCount + 1);
        var enabledNames = settings.EnabledVariations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = VariationRegistry.All.Where(definition => enabledNames.Contains(definition.Name)).ToArray();
        for (var i = 0; i < transformCount; i++)
        {
            genome.Transforms.Add(CreateTransform(
                random,
                settings,
                definitions,
                variationWeightSampler,
                i / (double)Math.Max(1, transformCount - 1),
                Math.Max(0.01, 1 + random.NextSigned(settings.TransformSelectionBalance))));
        }

        if (settings.AllowFinalTransforms && random.NextBool(settings.FinalTransformChance))
        {
            genome.FinalTransform = CreateTransform(random, settings, definitions, variationWeightSampler, 0.5, 0);
        }

        FlameValidator.ThrowIfInvalid(genome);
        return genome;
    }

    private static FlameTransform CreateTransform(
        DeterministicRandom random,
        RandomGeneratorSettings settings,
        IReadOnlyList<VariationDefinition> definitions,
        SpaceFillingSimplexSampler variationWeightSampler,
        double color,
        double selectionWeight)
    {
        var angle = DegreesToRadians(NextRange(random, settings.MinimumAffineRotationDegrees, settings.MaximumAffineRotationDegrees));
        var scale = NextRange(random, settings.MinimumAffineScale, settings.MaximumAffineScale);
        var shear = NextRange(random, settings.MinimumAffineShear, settings.MaximumAffineShear);
        var transform = new FlameTransform
        {
            Weight = selectionWeight,
            Color = color,
            Symmetry = 0.3,
            A = Math.Cos(angle) * scale,
            B = -Math.Sin(angle) * scale + shear,
            C = Math.Sin(angle) * scale,
            D = Math.Cos(angle) * scale,
            E = NextRange(random, -settings.TranslationExtent, settings.TranslationExtent),
            F = NextRange(random, -settings.TranslationExtent, settings.TranslationExtent)
        };

        var maximumVariationCount = Math.Min(settings.MaximumVariationCount, definitions.Count);
        var variationCount = random.NextInt(settings.MinimumVariationCount, maximumVariationCount + 1);
        var variationNames = new List<string>(variationCount);
        while (variationNames.Count < variationCount)
        {
            var definition = definitions[random.NextInt(0, definitions.Count)];
            if (variationNames.Contains(definition.Name, StringComparer.OrdinalIgnoreCase)) continue;
            variationNames.Add(definition.Name);
        }

        var variationWeights = variationWeightSampler.NextWeights(variationCount, settings.MinimumVariationShare, random);
        for (var i = 0; i < variationCount; i++) transform.Variations[variationNames[i]] = variationWeights[i];

        if (random.NextBool(settings.PostTransformChance))
        {
            var postAngle = DegreesToRadians(NextRange(random, settings.MinimumPostRotationDegrees, settings.MaximumPostRotationDegrees));
            var postScale = NextRange(random, settings.MinimumPostScale, settings.MaximumPostScale);
            transform.PostTransform = new AffineTransform(
                Math.Cos(postAngle) * postScale,
                -Math.Sin(postAngle) * postScale,
                Math.Sin(postAngle) * postScale,
                Math.Cos(postAngle) * postScale,
                NextRange(random, -settings.PostTranslationExtent, settings.PostTranslationExtent),
                NextRange(random, -settings.PostTranslationExtent, settings.PostTranslationExtent));
        }

        return transform;
    }

    private static int GenerateSymmetry(AllowedSymmetryTypes allowedTypes, DeterministicRandom random)
    {
        const double symmetryChance = 0.40;
        if (allowedTypes == AllowedSymmetryTypes.None || !random.NextBool(symmetryChance)) return 1;

        var choices = new List<AllowedSymmetryTypes>(3);
        if (allowedTypes.HasFlag(AllowedSymmetryTypes.Rotational)) choices.Add(AllowedSymmetryTypes.Rotational);
        if (allowedTypes.HasFlag(AllowedSymmetryTypes.Reflection)) choices.Add(AllowedSymmetryTypes.Reflection);
        if (allowedTypes.HasFlag(AllowedSymmetryTypes.Dihedral)) choices.Add(AllowedSymmetryTypes.Dihedral);
        var type = choices[random.NextInt(0, choices.Count)];
        var order = random.NextInt(2, 4);
        return type switch
        {
            AllowedSymmetryTypes.Reflection => -1,
            AllowedSymmetryTypes.Dihedral => -order,
            _ => order
        };
    }

    private static double NextRange(DeterministicRandom random, double minimum, double maximum) => minimum + random.NextDouble() * (maximum - minimum);
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180;
}
