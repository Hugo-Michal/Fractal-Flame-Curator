using FractalFlameCurator.Models;
using FractalFlameCurator.Storage;

namespace FractalFlameCurator.Pipeline;

public sealed class CandidateCatalog
{
    private readonly Dictionary<string, (RenderedArtifact Artifact, PreferenceScore? Score)> _candidates = new(StringComparer.OrdinalIgnoreCase);

    public void Refresh(SourceArchive archive, RatingStore ratings)
    {
        var ratedSourceIds = ratings.EnumerateRatedImagePaths()
            .Select(path => CandidateFileNaming.GetSourceId(Path.GetFileName(path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = archive.EnumerateArtifacts()
            .Where(artifact => !ratedSourceIds.Contains(artifact.SourceId))
            .GroupBy(artifact => artifact.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .Skip(1)
                .Any()
                    ? group.OrderByDescending(artifact => File.GetLastWriteTimeUtc(artifact.ImagePath))
                        .ThenByDescending(artifact => CandidateFileNaming.TryParseScore(artifact.BaseName, out _))
                        .First()
                    : group.First())
            .ToDictionary(artifact => artifact.SourceId, StringComparer.OrdinalIgnoreCase);
        foreach (var sourceId in _candidates.Keys.Except(current.Keys, StringComparer.OrdinalIgnoreCase).ToArray()) _candidates.Remove(sourceId);
        foreach (var artifact in current.Values) Upsert(artifact);
    }

    public void Upsert(RenderedArtifact artifact)
    {
        var existing = _candidates.GetValueOrDefault(artifact.SourceId);
        var parsedScore = CandidateFileNaming.TryParseScore(artifact.BaseName, out var score)
            ? new PreferenceScore(artifact.ImagePath, artifact.SourceId, score, 1 + score * 4, "filename")
            : existing.Score;
        _candidates[artifact.SourceId] = (artifact, parsedScore);
    }

    public bool Remove(string sourceId) => _candidates.Remove(sourceId);

    public RenderedArtifact? RecordScore(PreferenceScore score)
    {
        var sourceId = score.SourceId;
        if (!_candidates.TryGetValue(sourceId, out var candidate)) return null;

        var directory = Path.GetDirectoryName(score.ImagePath) ?? string.Empty;
        var flameFileName = CandidateFileNaming.WithScorePrefix(Path.GetFileName(candidate.Artifact.FlamePath), score.Score);
        var artifact = candidate.Artifact with
        {
            BaseName = Path.GetFileNameWithoutExtension(score.ImagePath),
            ImagePath = score.ImagePath,
            FlamePath = Path.Combine(directory, flameFileName)
        };
        _candidates[sourceId] = (artifact, score);
        return artifact;
    }

    public void ClearScore(string sourceId)
    {
        if (_candidates.TryGetValue(sourceId, out var candidate)) _candidates[sourceId] = (candidate.Artifact, null);
    }

    public PreferenceScore? GetScore(RenderedArtifact artifact) => _candidates.TryGetValue(artifact.SourceId, out var value) ? value.Score : null;

    public IReadOnlyList<RenderedArtifact> Ordered(bool aiEnabled)
    {
        var values = _candidates.Values;
        return (aiEnabled
            ? values.OrderByDescending(value => value.Score is not null)
                .ThenByDescending(value => value.Score?.Score ?? 0)
                .ThenByDescending(value => value.Artifact.SourceId, StringComparer.OrdinalIgnoreCase)
            : values.OrderBy(value => value.Artifact.SourceId, StringComparer.OrdinalIgnoreCase))
            .Select(value => value.Artifact)
            .ToArray();
    }

    public RenderedArtifact? Best(bool aiEnabled) => Ordered(aiEnabled).FirstOrDefault();

    public RenderedArtifact? FirstUnseen(bool aiEnabled, ISet<string> seenSourceIds) =>
        Ordered(aiEnabled).FirstOrDefault(artifact => !seenSourceIds.Contains(artifact.SourceId));

    public RenderedArtifact? NextUnseen(string sourceId, bool aiEnabled, ISet<string> seenSourceIds)
    {
        var ordered = Ordered(aiEnabled);
        var index = Array.FindIndex(ordered.ToArray(), artifact => string.Equals(artifact.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        var afterCurrent = index < 0 ? ordered : ordered.Skip(index + 1);
        return afterCurrent.FirstOrDefault(artifact => !seenSourceIds.Contains(artifact.SourceId))
            ?? ordered.FirstOrDefault(artifact => !seenSourceIds.Contains(artifact.SourceId));
    }

    public RenderedArtifact? Adjacent(string sourceId, int direction, bool aiEnabled)
    {
        var ordered = Ordered(aiEnabled);
        var index = Array.FindIndex(ordered.ToArray(), artifact => string.Equals(artifact.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return ordered.FirstOrDefault();
        var next = index + Math.Sign(direction);
        return next >= 0 && next < ordered.Count ? ordered[next] : null;
    }
}
