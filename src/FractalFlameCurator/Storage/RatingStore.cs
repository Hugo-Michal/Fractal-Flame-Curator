using FractalFlameCurator.Models;

namespace FractalFlameCurator.Storage;

public sealed record RatingAction(string ImagePath, string FlamePath, int NewRating, int? PreviousRating, bool CopyFiles);

public sealed class RatingStore
{
    private readonly Stack<RatingAction> _history = new();

    public RatingStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        RatingsDirectory = Path.Combine(RootDirectory, "ratings");
        for (var rating = 1; rating <= 5; rating++) Directory.CreateDirectory(GetRatingDirectory(rating));
    }

    public string RootDirectory { get; }
    public string RatingsDirectory { get; }

    public RatingAction Rate(string sourceImagePath, int rating, bool copyFiles = false)
    {
        if (rating is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(rating));
        if (!File.Exists(sourceImagePath)) throw new FileNotFoundException("The source image is not available.", sourceImagePath);
        if (!string.Equals(Path.GetExtension(sourceImagePath), ".png", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Only rendered PNG images can be rated.");
        sourceImagePath = Path.GetFullPath(sourceImagePath);
        var sourceFlamePath = SourceArchive.FindMatchingFlamePath(sourceImagePath)
            ?? throw new FileNotFoundException("The matching .flame source is not available.", sourceImagePath);
        var previous = FindRating(sourceImagePath);
        var action = new RatingAction(sourceImagePath, sourceFlamePath, rating, previous, copyFiles);
        var stableBaseName = CandidateFileNaming.RemoveScorePrefix(Path.GetFileName(sourceImagePath));
        var destinationImagePath = Path.Combine(GetRatingDirectory(rating), stableBaseName);
        var destinationFlamePath = Path.ChangeExtension(destinationImagePath, ".flame");
        if (copyFiles) CopyPair(sourceImagePath, sourceFlamePath, destinationImagePath, destinationFlamePath);
        else MovePair(sourceImagePath, sourceFlamePath, destinationImagePath, destinationFlamePath);
        RemoveOtherRatingCopies(sourceImagePath, destinationImagePath, destinationFlamePath);
        _history.Push(action);
        return action;
    }

    public bool Undo()
    {
        if (_history.Count == 0) return false;
        var action = _history.Peek();
        var currentImagePath = FindRatingImage(action.ImagePath);
        if (currentImagePath is null) return false;
        var currentFlamePath = SourceArchive.FindMatchingFlamePath(currentImagePath)
            ?? throw new FileNotFoundException("The matching .flame source is not available.", currentImagePath);
        if (action.CopyFiles)
        {
            if (action.PreviousRating is { } priorRating)
            {
                var stableBaseName = CandidateFileNaming.RemoveScorePrefix(Path.GetFileName(action.ImagePath));
                var destinationImagePath = Path.Combine(GetRatingDirectory(priorRating), stableBaseName);
                var destinationFlamePath = Path.ChangeExtension(destinationImagePath, ".flame");
                var sourcePair = FindRenderedPair(CandidateFileNaming.GetSourceId(Path.GetFileName(action.ImagePath)))
                    ?? (currentImagePath, currentFlamePath);
                CopyPair(sourcePair.ImagePath, sourcePair.FlamePath, destinationImagePath, destinationFlamePath);
                RemoveOtherRatingCopies(action.ImagePath, destinationImagePath, destinationFlamePath);
            }
            else
            {
                File.Delete(currentImagePath);
                File.Delete(currentFlamePath);
            }

            _history.Pop();
            return true;
        }

        if (action.PreviousRating is { } previous)
        {
            var stableBaseName = CandidateFileNaming.RemoveScorePrefix(Path.GetFileName(action.ImagePath));
            var destinationImagePath = Path.Combine(GetRatingDirectory(previous), stableBaseName);
            MovePair(currentImagePath, currentFlamePath, destinationImagePath, Path.ChangeExtension(destinationImagePath, ".flame"));
        }
        else MovePair(currentImagePath, currentFlamePath, action.ImagePath, action.FlamePath);
        RemoveOtherRatingCopies(action.ImagePath,
            action.PreviousRating is { } previousRating
                ? Path.Combine(GetRatingDirectory(previousRating), CandidateFileNaming.RemoveScorePrefix(Path.GetFileName(action.ImagePath)))
                : action.ImagePath,
            action.PreviousRating is { } previousRatingForFlame
                ? Path.Combine(GetRatingDirectory(previousRatingForFlame), Path.GetFileNameWithoutExtension(CandidateFileNaming.RemoveScorePrefix(Path.GetFileName(action.ImagePath))) + ".flame")
                : action.FlamePath);
        _history.Pop();
        return true;
    }

    public int? FindRating(string sourceImagePath)
    {
        var stableFileName = CandidateFileNaming.RemoveScorePrefix(Path.GetFileName(sourceImagePath));
        for (var rating = 1; rating <= 5; rating++)
        {
            if (Directory.EnumerateFiles(GetRatingDirectory(rating), "*.png", SearchOption.TopDirectoryOnly)
                .Any(path => string.Equals(CandidateFileNaming.RemoveScorePrefix(Path.GetFileName(path)), stableFileName, StringComparison.OrdinalIgnoreCase))) return rating;
        }
        return null;
    }

    public IReadOnlyList<string> EnumerateRatedImagePaths()
    {
        return Enumerable.Range(1, 5)
            .SelectMany(rating => Directory.EnumerateFiles(GetRatingDirectory(rating), "*.png", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<RenderedArtifact> EnumerateRatedArtifacts()
    {
        return Enumerable.Range(1, 5)
            .SelectMany(rating => Directory.EnumerateFiles(GetRatingDirectory(rating), "*.png", SearchOption.TopDirectoryOnly))
            .Where(SourceArchive.IsCompleteCandidate)
            .Select(path =>
            {
                var flamePath = SourceArchive.FindMatchingFlamePath(path)!;
                return new RenderedArtifact(Path.GetFileNameWithoutExtension(path), path, flamePath, 0, 0);
            })
            .OrderBy(artifact => artifact.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool RatingFoldersContainPairedFiles()
    {
        for (var rating = 1; rating <= 5; rating++)
        {
            var files = Directory.EnumerateFiles(GetRatingDirectory(rating), "*", SearchOption.TopDirectoryOnly).ToArray();
            if (files.Any(path => !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)
                                  && !string.Equals(Path.GetExtension(path), ".flame", StringComparison.OrdinalIgnoreCase))) return false;

            var images = files.Where(path => string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase)).ToArray();
            var flames = files.Where(path => string.Equals(Path.GetExtension(path), ".flame", StringComparison.OrdinalIgnoreCase)).ToArray();
            var imageSourceIds = images.Select(path => CandidateFileNaming.GetSourceId(Path.GetFileName(path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var flameSourceIds = flames.Select(path => CandidateFileNaming.GetSourceId(Path.GetFileName(path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (imageSourceIds.Count != images.Length || flameSourceIds.Count != flames.Length || !imageSourceIds.SetEquals(flameSourceIds)) return false;
        }
        return true;
    }

    private string GetRatingDirectory(int rating) => Path.Combine(RatingsDirectory, rating.ToString());

    private string? FindRatingImage(string sourceImagePath)
    {
        for (var rating = 1; rating <= 5; rating++)
        {
            var match = Directory.EnumerateFiles(GetRatingDirectory(rating), "*.png", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(CandidateFileNaming.GetSourceId(Path.GetFileName(path)), CandidateFileNaming.GetSourceId(Path.GetFileName(sourceImagePath)), StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return null;
    }

    private (string ImagePath, string FlamePath)? FindRenderedPair(string sourceId)
    {
        var renderedDirectory = Path.Combine(RootDirectory, "rendered");
        if (!Directory.Exists(renderedDirectory)) return null;
        foreach (var imagePath in Directory.EnumerateFiles(renderedDirectory, "*.png", SearchOption.TopDirectoryOnly))
        {
            if (!string.Equals(CandidateFileNaming.GetSourceId(Path.GetFileName(imagePath)), sourceId, StringComparison.OrdinalIgnoreCase)) continue;
            var flamePath = SourceArchive.FindMatchingFlamePath(imagePath);
            if (flamePath is not null && SourceArchive.IsCompleteCandidate(imagePath)) return (imagePath, flamePath);
        }
        return null;
    }

    private void RemoveOtherRatingCopies(string sourceImagePath, string keepImagePath, string keepFlamePath)
    {
        var sourceId = CandidateFileNaming.GetSourceId(Path.GetFileName(sourceImagePath));
        foreach (var path in Enumerable.Range(1, 5).SelectMany(rating => Directory.EnumerateFiles(GetRatingDirectory(rating), "*", SearchOption.TopDirectoryOnly))
                     .Where(path => string.Equals(CandidateFileNaming.GetSourceId(Path.GetFileName(path)), sourceId, StringComparison.OrdinalIgnoreCase))
                     .Where(path => !string.Equals(path, keepImagePath, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, keepFlamePath, StringComparison.OrdinalIgnoreCase)))
        {
            File.Delete(path);
        }
    }

    private static void MovePair(string sourceImagePath, string sourceFlamePath, string destinationImagePath, string destinationFlamePath)
    {
        sourceImagePath = Path.GetFullPath(sourceImagePath);
        sourceFlamePath = Path.GetFullPath(sourceFlamePath);
        destinationImagePath = Path.GetFullPath(destinationImagePath);
        destinationFlamePath = Path.GetFullPath(destinationFlamePath);
        if (string.Equals(sourceImagePath, destinationImagePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sourceFlamePath, destinationFlamePath, StringComparison.OrdinalIgnoreCase)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationImagePath)!);
        var token = Guid.NewGuid().ToString("N");
        var temporaryImagePath = sourceImagePath + ".rating-" + token + ".tmp";
        var temporaryFlamePath = sourceFlamePath + ".rating-" + token + ".tmp";
        var imagePublished = false;
        var flamePublished = false;
        File.Move(sourceImagePath, temporaryImagePath);
        try
        {
            File.Move(sourceFlamePath, temporaryFlamePath);
            try
            {
                if (File.Exists(destinationImagePath)) File.Delete(destinationImagePath);
                if (File.Exists(destinationFlamePath)) File.Delete(destinationFlamePath);
                File.Move(temporaryImagePath, destinationImagePath);
                imagePublished = true;
                File.Move(temporaryFlamePath, destinationFlamePath);
                flamePublished = true;
            }
            catch
            {
                if (imagePublished && File.Exists(destinationImagePath) && !File.Exists(sourceImagePath)) File.Move(destinationImagePath, sourceImagePath);
                else if (File.Exists(temporaryImagePath) && !File.Exists(sourceImagePath)) File.Move(temporaryImagePath, sourceImagePath);
                if (flamePublished && File.Exists(destinationFlamePath) && !File.Exists(sourceFlamePath)) File.Move(destinationFlamePath, sourceFlamePath);
                else if (File.Exists(temporaryFlamePath) && !File.Exists(sourceFlamePath)) File.Move(temporaryFlamePath, sourceFlamePath);
                throw;
            }
        }
        catch
        {
            if (File.Exists(temporaryImagePath) && !File.Exists(sourceImagePath)) File.Move(temporaryImagePath, sourceImagePath);
            throw;
        }
    }

    private static void CopyPair(string sourceImagePath, string sourceFlamePath, string destinationImagePath, string destinationFlamePath)
    {
        sourceImagePath = Path.GetFullPath(sourceImagePath);
        sourceFlamePath = Path.GetFullPath(sourceFlamePath);
        destinationImagePath = Path.GetFullPath(destinationImagePath);
        destinationFlamePath = Path.GetFullPath(destinationFlamePath);
        if (string.Equals(sourceImagePath, destinationImagePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sourceFlamePath, destinationFlamePath, StringComparison.OrdinalIgnoreCase)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(destinationImagePath)!);
        var token = Guid.NewGuid().ToString("N");
        var temporaryImagePath = destinationImagePath + ".rating-" + token + ".tmp";
        var temporaryFlamePath = destinationFlamePath + ".rating-" + token + ".tmp";
        var imagePublished = false;
        var flamePublished = false;
        try
        {
            File.Copy(sourceImagePath, temporaryImagePath, true);
            File.Copy(sourceFlamePath, temporaryFlamePath, true);
            if (File.Exists(destinationImagePath)) File.Delete(destinationImagePath);
            if (File.Exists(destinationFlamePath)) File.Delete(destinationFlamePath);
            File.Move(temporaryImagePath, destinationImagePath);
            imagePublished = true;
            File.Move(temporaryFlamePath, destinationFlamePath);
            flamePublished = true;
        }
        catch
        {
            if (imagePublished && File.Exists(destinationImagePath)) File.Delete(destinationImagePath);
            if (flamePublished && File.Exists(destinationFlamePath)) File.Delete(destinationFlamePath);
            if (File.Exists(temporaryImagePath)) File.Delete(temporaryImagePath);
            if (File.Exists(temporaryFlamePath)) File.Delete(temporaryFlamePath);
            throw;
        }
    }
}
