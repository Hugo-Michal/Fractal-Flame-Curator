using FractalFlameCurator.Models;
using FractalFlameCurator.Storage;

namespace FractalFlameCurator.Pipeline;

public sealed record RatingMismatch(RenderedArtifact Artifact, int Rating, double Deviation);

public static class RatingMismatchCatalog
{
    public static IReadOnlyList<RatingMismatch> Build(RatingStore ratings)
    {
        return ratings.EnumerateRatedArtifactsWithRatings()
            .Where(rated => CandidateFileNaming.TryParseScore(rated.Artifact.BaseName, out _))
            .Select(rated => new RatingMismatch(rated.Artifact, rated.Rating, Deviation(rated.Rating, Score(rated.Artifact.BaseName))))
            .Where(mismatch => mismatch.Deviation > 0)
            .OrderByDescending(mismatch => mismatch.Deviation)
            .ThenBy(mismatch => mismatch.Artifact.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static double Deviation(int rating, double score)
    {
        if (rating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));
        var expectedRating = 1 + Math.Clamp(score, 0, 1) * 4;
        var lower = rating == 1 ? 1 : rating - 0.5;
        var upper = rating == 5 ? 5 : rating + 0.5;
        return expectedRating < lower ? lower - expectedRating : expectedRating > upper ? expectedRating - upper : 0;
    }

    private static double Score(string baseName)
    {
        CandidateFileNaming.TryParseScore(baseName, out var score);
        return score;
    }
}
