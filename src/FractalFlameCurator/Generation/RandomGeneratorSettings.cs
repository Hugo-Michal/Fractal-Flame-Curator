using FractalFlameCurator.Models;

namespace FractalFlameCurator.Generation;

[Flags]
public enum AllowedSymmetryTypes
{
    None = 0,
    Rotational = 1,
    Reflection = 2,
    Dihedral = 4
}

public sealed record RandomGeneratorSettings
{
    public int MinimumTransformCount { get; init; } = 2;
    public int MaximumTransformCount { get; init; } = 5;
    public AllowedSymmetryTypes SymmetryTypes { get; init; } = AllowedSymmetryTypes.Rotational;
    public double MinimumAffineRotationDegrees { get; init; } = -180;
    public double MaximumAffineRotationDegrees { get; init; } = 180;
    public double MinimumAffineScale { get; init; } = 0.35;
    public double MaximumAffineScale { get; init; } = 0.85;
    public double MinimumAffineShear { get; init; } = -0.25;
    public double MaximumAffineShear { get; init; } = 0.25;
    public double TranslationExtent { get; init; } = 0.75;
    public double TransformSelectionBalance { get; init; } = 0.35;
    public int MinimumVariationCount { get; init; } = 1;
    public int MaximumVariationCount { get; init; } = 3;
    public string[] EnabledVariations { get; init; } = VariationRegistry.All.Select(definition => definition.Name).ToArray();
    public double VariationBlendDominance { get; init; } = 0.5;
    public double PostTransformChance { get; init; } = 0.42;
    public double MinimumPostRotationDegrees { get; init; } = -180;
    public double MaximumPostRotationDegrees { get; init; } = 180;
    public double MinimumPostScale { get; init; } = 0.75;
    public double MaximumPostScale { get; init; } = 1.25;
    public double PostTranslationExtent { get; init; } = 0.18;
    public bool AllowFinalTransforms { get; init; }
    public double FinalTransformChance { get; init; } = 0.15;

    public static RandomGeneratorSettings CreateDefault() => new();

    public void ThrowIfInvalid()
    {
        if (MinimumTransformCount is < 2 or > 12 || MaximumTransformCount is < 2 or > 12 || MinimumTransformCount > MaximumTransformCount)
            throw new InvalidDataException("Transform count must be an ordered range between 2 and 12.");
        if ((SymmetryTypes & ~(AllowedSymmetryTypes.Rotational | AllowedSymmetryTypes.Reflection | AllowedSymmetryTypes.Dihedral)) != 0)
            throw new InvalidDataException("The selected symmetry type is not supported.");
        ValidateRange(MinimumAffineRotationDegrees, MaximumAffineRotationDegrees, -180, 180, "Affine rotation");
        ValidateRange(MinimumAffineScale, MaximumAffineScale, 0.10, 1.25, "Affine scale");
        ValidateRange(MinimumAffineShear, MaximumAffineShear, -1, 1, "Affine shear");
        ValidateValue(TranslationExtent, 0, 2, "Translation extent");
        ValidateValue(TransformSelectionBalance, 0, 1, "Transform balance");
        if (MinimumVariationCount is < 1 or > 5 || MaximumVariationCount is < 1 or > 5 || MinimumVariationCount > MaximumVariationCount)
            throw new InvalidDataException("Variation count must be an ordered range between 1 and 5.");

        var enabled = EnabledVariations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (enabled.Length == 0) throw new InvalidDataException("Enable at least one variation type.");
        if (MinimumVariationCount > enabled.Length) throw new InvalidDataException("The minimum variation count cannot exceed the number of enabled variation types.");
        var unknown = enabled.FirstOrDefault(name => !VariationRegistry.Names.Contains(name));
        if (unknown is not null) throw new InvalidDataException($"Unknown variation '{unknown}'.");

        ValidateValue(VariationBlendDominance, 0, 1, "Variation blend dominance");
        ValidateValue(PostTransformChance, 0, 1, "Post-transform chance");
        ValidateRange(MinimumPostRotationDegrees, MaximumPostRotationDegrees, -180, 180, "Post-transform rotation");
        ValidateRange(MinimumPostScale, MaximumPostScale, 0.25, 2, "Post-transform scale");
        ValidateValue(PostTranslationExtent, 0, 1, "Post-transform translation extent");
        ValidateValue(FinalTransformChance, 0, 1, "Final-transform chance");
    }

    public RandomGeneratorSettings Snapshot()
    {
        ThrowIfInvalid();
        return this with { EnabledVariations = EnabledVariations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
    }

    private static void ValidateRange(double minimum, double maximum, double lowerBound, double upperBound, string name)
    {
        ValidateValue(minimum, lowerBound, upperBound, name);
        ValidateValue(maximum, lowerBound, upperBound, name);
        if (minimum > maximum) throw new InvalidDataException($"{name} minimum cannot exceed its maximum.");
    }

    private static void ValidateValue(double value, double minimum, double maximum, string name)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidDataException($"{name} must be between {minimum} and {maximum}.");
    }
}
