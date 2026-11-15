using FractalFlameCurator.Models;

namespace FractalFlameCurator.Generation;

public static class FlameValidator
{
    public static IReadOnlyList<string> Validate(FlameGenome genome)
    {
        var errors = new List<string>();
        if (genome.Width <= 0 || genome.Height <= 0) errors.Add("Image dimensions must be positive.");
        if (genome.Width != genome.Height) errors.Add("Phase 1 flames must use a square render.");
        if (!double.IsFinite(genome.CenterX) || !double.IsFinite(genome.CenterY) || !double.IsFinite(genome.Rotate)) errors.Add("Camera center and rotation must be finite.");
        if (genome.Scale is < 20 or > 400 || !double.IsFinite(genome.Scale)) errors.Add("Camera scale is outside the bounded range.");
        if (genome.Transforms.Count is < 2 or > 12) errors.Add("A flame must contain between 2 and 12 transforms.");
        if (genome.Palette.Colors.Count != 256) errors.Add("A flame palette must contain 256 colors.");
        if (genome.Oversample is < 1 or > 6) errors.Add("Oversample must be between 1 and 6.");
        if (genome.Quality < 100) errors.Add("Sample budget is too small to render reliably.");

        var totalWeight = 0d;
        foreach (var transform in genome.Transforms)
        {
            ValidateTransform(transform, errors, requirePositiveWeight: true);
            totalWeight += transform.Weight;
        }
        if (genome.FinalTransform is not null) ValidateTransform(genome.FinalTransform, errors, requirePositiveWeight: false);
        if (!IsFinite(totalWeight) || totalWeight <= 0) errors.Add("The flame has no usable transform weight.");
        return errors;
    }

    public static void ThrowIfInvalid(FlameGenome genome)
    {
        var errors = Validate(genome);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(" ", errors));
    }

    private static bool IsFinite(double value) => double.IsFinite(value);

    private static void ValidateTransform(FlameTransform transform, ICollection<string> errors, bool requirePositiveWeight)
    {
        if (requirePositiveWeight && (!IsFinite(transform.Weight) || transform.Weight <= 0)) errors.Add("Transform weights must be finite and positive.");
        if (!IsFinite(transform.A) || !IsFinite(transform.B) || !IsFinite(transform.C) || !IsFinite(transform.D) || !IsFinite(transform.E) || !IsFinite(transform.F))
            errors.Add("Affine coefficients must be finite.");
        if (Math.Abs(transform.A * transform.D - transform.B * transform.C) < 0.01) errors.Add("An affine transform is numerically singular.");
        if (new[] { transform.A, transform.B, transform.C, transform.D, transform.E, transform.F }.Any(value => Math.Abs(value) > 3.5)) errors.Add("Affine coefficients exceed the safe bound.");
        if (transform.Variations.Count == 0 || transform.Variations.All(pair => pair.Value <= 0)) errors.Add("Every transform needs a positive variation.");
        foreach (var pair in transform.Variations)
        {
            if (!VariationRegistry.Names.Contains(pair.Key)) errors.Add($"Unknown variation '{pair.Key}'.");
            if (!IsFinite(pair.Value) || pair.Value < 0 || pair.Value > 2) errors.Add($"Variation weight '{pair.Key}' is outside the safe range.");
        }
    }
}

