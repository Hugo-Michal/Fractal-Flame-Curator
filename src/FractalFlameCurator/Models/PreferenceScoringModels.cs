using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace FractalFlameCurator.Models;

public static class OrdinalPreferenceMath
{
    public const int ThresholdCount = 4;

    public static bool[] ThresholdTargets(int rating)
    {
        if (rating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));
        return Enumerable.Range(2, ThresholdCount).Select(threshold => rating >= threshold).ToArray();
    }

    public static double ExpectedRating(IReadOnlyList<double> cumulativeProbabilities)
    {
        if (cumulativeProbabilities.Count != ThresholdCount) throw new ArgumentException("Four ordinal threshold probabilities are required.", nameof(cumulativeProbabilities));
        return Math.Clamp(1 + cumulativeProbabilities.Sum(probability => Math.Clamp(probability, 0, 1)), 1, 5);
    }

    public static double ScoreFromExpectedRating(double expectedRating) => Math.Clamp((expectedRating - 1) / 4, 0, 1);

    public static double ScoreFromCumulativeProbabilities(IReadOnlyList<double> cumulativeProbabilities) => ScoreFromExpectedRating(ExpectedRating(cumulativeProbabilities));
}

public static class CandidateFileNaming
{
    private static readonly Regex ScorePrefix = new("^(?<score>\\d{6})__(?<source>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RemoveScorePrefix(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var match = ScorePrefix.Match(stem);
        return (match.Success ? match.Groups["source"].Value : stem) + extension;
    }

    public static string GetSourceId(string fileName) => Path.GetFileNameWithoutExtension(RemoveScorePrefix(fileName));

    public static bool IsSupportedRasterImagePath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatScore(double score)
    {
        var integer = Math.Clamp((int)Math.Round(Math.Clamp(score, 0, 1) * 100000, MidpointRounding.AwayFromZero), 0, 100000);
        return integer.ToString("000000", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string WithScorePrefix(string fileName, double score)
    {
        var stableName = RemoveScorePrefix(fileName);
        return $"{FormatScore(score)}__{stableName}";
    }

    public static bool TryParseScore(string fileName, out double score)
    {
        var match = ScorePrefix.Match(Path.GetFileNameWithoutExtension(fileName));
        if (match.Success && int.TryParse(match.Groups["score"].Value, out var integer))
        {
            score = integer / 100000d;
            return true;
        }
        score = 0;
        return false;
    }

}

public sealed record DatasetImage(string ImagePath, int Rating, string SourceId);

public sealed record DatasetSplit(
    IReadOnlyList<DatasetImage> Train,
    IReadOnlyList<DatasetImage> Validation,
    IReadOnlyList<DatasetImage> Test,
    bool IsReliable)
{
    public int TotalCount => Train.Count + Validation.Count + Test.Count;
}

public enum DatasetReadinessColor
{
    Red,
    Amber,
    Green
}

public sealed record DatasetStatistics(
    IReadOnlyDictionary<int, int> Counts,
    int Total,
    DatasetReadinessColor Readiness,
    string ReadinessMessage)
{
    public int CountFor(int rating) => Counts.TryGetValue(rating, out var count) ? count : 0;

    public static DatasetStatistics FromCounts(IReadOnlyDictionary<int, int> counts)
    {
        var normalized = new ReadOnlyDictionary<int, int>(Enumerable.Range(1, 5).ToDictionary(rating => rating, rating => Math.Max(0, counts.GetValueOrDefault(rating))));
        var total = normalized.Values.Sum();
        var minimum = normalized.Values.Min();
        var readiness = total < 20 || minimum == 0
            ? DatasetReadinessColor.Red
            : total < 50 || minimum < 5
                ? DatasetReadinessColor.Amber
                : DatasetReadinessColor.Green;
        var message = readiness switch
        {
            DatasetReadinessColor.Red => "Very small or missing data; training is allowed, but evaluation will be unreliable.",
            DatasetReadinessColor.Amber => "Usable but weak coverage; treat validation and test metrics cautiously.",
            _ => "Reasonably balanced for a first model; this heuristic is not a scientific guarantee."
        };
        return new DatasetStatistics(normalized, total, readiness, message);
    }
}

public sealed record DatasetSnapshot(
    string RootDirectory,
    IReadOnlyList<DatasetImage> Images,
    DatasetStatistics Statistics,
    DatasetSplit Split,
    DateTimeOffset CapturedAt);

public sealed record DeviceDiagnostics(
    bool PythonAvailable,
    bool TorchAvailable,
    bool CudaAvailable,
    string GpuName,
    string PyTorchVersion,
    string ActiveDevice,
    bool AiReady,
    string Detail)
{
    public static DeviceDiagnostics Unavailable(string detail) => new(false, false, false, "Unavailable", "Unavailable", "none", false, detail);
}

public sealed record PreferenceScore(string ImagePath, string SourceId, double Score, double ExpectedRating, string ModelVersion);

public sealed record ControlMetric(string Name, double? Score, string Interpretation);

public sealed record TrainingMetrics(
    double OrdinalAccuracy,
    double MeanAbsoluteRatingError,
    double SpearmanCorrelation,
    double RankCorrelation,
    double CalibrationError,
    bool IsReliable,
    IReadOnlyList<ControlMetric> Controls);

public sealed record TrainingResult(string ModelVersion, DatasetStatistics Statistics, DatasetSplit Split, TrainingMetrics Metrics);
