using FractalFlameCurator.Models;

namespace FractalFlameCurator.Ai;

public static class PreferenceDatasetBuilder
{
    public static DatasetSnapshot Snapshot(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var images = new List<DatasetImage>();
        var counts = new Dictionary<int, int>();
        for (var rating = 1; rating <= 5; rating++)
        {
            var directory = Path.Combine(root, "ratings", rating.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                         .Where(CandidateFileNaming.IsSupportedRasterImagePath)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                images.Add(new DatasetImage(path, rating, CandidateFileNaming.GetSourceId(Path.GetFileName(path))));
                counts[rating] = counts.GetValueOrDefault(rating) + 1;
            }
        }

        var statistics = DatasetStatistics.FromCounts(counts);
        return new DatasetSnapshot(root, images, statistics, Split(images), DateTimeOffset.UtcNow);
    }

    public static DatasetSplit Split(IReadOnlyList<DatasetImage> images)
    {
        var groups = images.GroupBy(image => image.SourceId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => StableBucket(group.Key))
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var train = new List<DatasetImage>();
        var validation = new List<DatasetImage>();
        var test = new List<DatasetImage>();
        foreach (var group in groups)
        {
            var bucket = StableBucket(group.Key) % 10;
            (bucket < 7 ? train : bucket < 9 ? validation : test).AddRange(group);
        }
        // A one-group or very small corpus is still trainable. Move one group
        // into training when hashing would otherwise leave the train split empty;
        // reliability remains false because validation/test coverage is absent.
        if (train.Count == 0 && groups.Length > 0)
        {
            train.AddRange(validation);
            train.AddRange(test);
            validation.Clear();
            test.Clear();
        }
        var reliable = groups.Length >= 10 && train.Count > 0 && validation.Count > 0 && test.Count > 0;
        return new DatasetSplit(train, validation, test, reliable);
    }

    public static IReadOnlyList<(string Name, string ImagePath)> ControlImages(string rootDirectory)
    {
        var controlsRoot = Path.Combine(Path.GetFullPath(rootDirectory), "controls");
        if (!Directory.Exists(controlsRoot)) return [];
        return Directory.EnumerateDirectories(controlsRoot, "*", SearchOption.TopDirectoryOnly)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(CandidateFileNaming.IsSupportedRasterImagePath)
                .Select(path => (Name: Path.GetFileName(directory), ImagePath: path)))
            .OrderBy(control => control.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(control => control.ImagePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int StableBucket(string value)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var character in value.ToUpperInvariant()) hash = (hash ^ character) * 16777619;
            return (int)((uint)hash % 10);
        }
    }
}
