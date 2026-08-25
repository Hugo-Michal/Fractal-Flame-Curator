using System.Text.Json;
using System.Text.Json.Serialization;
using FractalFlameCurator.Generation;
using FractalFlameCurator.Models;
using FractalFlameCurator.Rendering;
using FractalFlameCurator.Serialization;

namespace FractalFlameCurator.Storage;

public sealed record RenderedArtifact(string BaseName, string ImagePath, string FlamePath, long Seed, int Sequence)
{
    public string SourceId => CandidateFileNaming.GetSourceId(BaseName);
}

public sealed class SourceArchive
{
    public SourceArchive(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        RenderedDirectory = Path.Combine(RootDirectory, "rendered");
        Directory.CreateDirectory(RenderedDirectory);
    }

    public string RootDirectory { get; }
    public string RenderedDirectory { get; }

    public IReadOnlyList<RenderedArtifact> EnumerateArtifacts()
    {
        if (!Directory.Exists(RenderedDirectory)) return [];
        return Directory.EnumerateFiles(RenderedDirectory, "*.png", SearchOption.TopDirectoryOnly)
            .Where(IsCompleteCandidate)
            .Select(path =>
            {
                var baseName = Path.GetFileNameWithoutExtension(path);
                var sourceId = CandidateFileNaming.GetSourceId(baseName);
                var flamePath = FindMatchingFlamePath(path)!;
                return new RenderedArtifact(baseName, path, flamePath, 0, ParseSequence(sourceId));
            })
            .OrderBy(artifact => artifact.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string SaveGeneratorProfile(string sessionId, long sessionSeed, RandomGeneratorSettings settings)
    {
        var path = Path.Combine(RenderedDirectory, $"generator_profile_run_{SanitizeSessionId(sessionId)}.json");
        var temporaryPath = path + ".tmp";
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        var profile = new
        {
            ProfileVersion = 1,
            VariationWeightSamplerVersion = SpaceFillingSimplexSampler.Version,
            SessionId = sessionId,
            SessionSeed = sessionSeed,
            GeneratorSettings = settings.Snapshot()
        };
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(profile, options));
            File.Move(temporaryPath, path, true);
            return path;
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static bool IsCompleteCandidate(string imagePath)
    {
        if (!File.Exists(imagePath)) return false;
        var flamePath = FindMatchingFlamePath(imagePath);
        if (flamePath is null) return false;
        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var flameStream = new FileStream(flamePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length > 0 && flameStream.Length > 0;
        }
        catch (IOException) { return false; }
    }

    public static string? FindMatchingFlamePath(string imagePath)
    {
        var directory = Path.GetDirectoryName(imagePath) ?? ".";
        if (!Directory.Exists(directory)) return null;
        var exactPath = Path.Combine(directory, Path.GetFileNameWithoutExtension(imagePath) + ".flame");
        if (File.Exists(exactPath)) return exactPath;
        var sourceId = CandidateFileNaming.GetSourceId(Path.GetFileName(imagePath));
        return Directory.EnumerateFiles(directory, "*.flame", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(CandidateFileNaming.GetSourceId(Path.GetFileName(path)), sourceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public RenderedArtifact Save(FlameGenome genome, RenderedFrame frame, int sequence, string? sessionId = null)
    {
        var runSuffix = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : $"_run_{SanitizeSessionId(sessionId)}";
        var baseName = $"flame_{sequence:000000}_seed_{genome.Seed}{runSuffix}";
        var imagePath = Path.Combine(RenderedDirectory, baseName + ".png");
        var flamePath = Path.Combine(RenderedDirectory, baseName + ".flame");
        var imageTemp = imagePath + ".tmp";
        var flameTemp = flamePath + ".tmp";
        try
        {
            frame.SavePng(imageTemp);
            FlameXmlSerializer.Save(genome, flameTemp);
            File.Move(flameTemp, flamePath, true);
            // Publish the image last. A watcher can therefore only observe a candidate
            // after both the PNG and its stable source genome are complete.
            File.Move(imageTemp, imagePath, true);
            return new RenderedArtifact(baseName, imagePath, flamePath, genome.Seed, sequence);
        }
        finally
        {
            if (File.Exists(imageTemp)) File.Delete(imageTemp);
            if (File.Exists(flameTemp)) File.Delete(flameTemp);
        }
    }

    private static int ParseSequence(string sourceId)
    {
        var marker = "flame_";
        var start = sourceId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return 0;
        var valueStart = start + marker.Length;
        var value = sourceId[valueStart..].TakeWhile(char.IsDigit).ToArray();
        return int.TryParse(new string(value), out var sequence) ? sequence : 0;
    }

    private static string SanitizeSessionId(string sessionId)
    {
        var safe = new string(sessionId.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "session" : safe;
    }
}
