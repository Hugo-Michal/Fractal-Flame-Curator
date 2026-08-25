using FractalFlameCurator.Ai;
using FractalFlameCurator.Models;
using FractalFlameCurator.Pipeline;
using FractalFlameCurator.Rendering;
using FractalFlameCurator.Storage;
using Xunit;

namespace FractalFlameCurator.Tests;

public sealed class PhaseTwoTests
{
    [Theory]
    [InlineData(1, false, false, false, false)]
    [InlineData(2, true, false, false, false)]
    [InlineData(3, true, true, false, false)]
    [InlineData(4, true, true, true, false)]
    [InlineData(5, true, true, true, true)]
    public void RatingFoldersBecomeOrdinalThresholdTargets(int rating, bool atLeastTwo, bool atLeastThree, bool atLeastFour, bool atLeastFive)
    {
        Assert.Equal([atLeastTwo, atLeastThree, atLeastFour, atLeastFive], OrdinalPreferenceMath.ThresholdTargets(rating));
    }

    [Fact]
    public void ExpectedRatingAndRuntimeScoreUseCumulativeProbabilities()
    {
        var expected = OrdinalPreferenceMath.ExpectedRating([0.2, 0.4, 0.6, 0.8]);
        Assert.Equal(3, expected, 6);
        Assert.Equal(0.5, OrdinalPreferenceMath.ScoreFromExpectedRating(expected), 6);
        Assert.Equal(0.5, OrdinalPreferenceMath.ScoreFromCumulativeProbabilities([0.2, 0.4, 0.6, 0.8]), 6);
    }

    [Fact]
    public void FilenameScorePrefixIsFixedWidthAndStableIdIsPreserved()
    {
        var name = "flame_000142_seed_123.png";
        Assert.Equal("087342__flame_000142_seed_123.png", CandidateFileNaming.WithScorePrefix(name, 0.87342));
        Assert.Equal(name, CandidateFileNaming.RemoveScorePrefix(CandidateFileNaming.WithScorePrefix(name, 0.87342)));
        Assert.Equal("flame_000142_seed_123", CandidateFileNaming.GetSourceId("087342__flame_000142_seed_123.png"));
        Assert.Equal("100000", CandidateFileNaming.FormatScore(1));
    }

    [Theory]
    [InlineData("000001__flame_b.png", 0.00001)]
    [InlineData("100000__flame_c.png", 1)]
    [InlineData("087342__flame_a.png", 0.87342)]
    public void FixedWidthPrefixesRoundTripTheirScores(string fileName, double expected)
    {
        Assert.True(CandidateFileNaming.TryParseScore(fileName, out var actual));
        Assert.Equal(expected, actual, 6);
    }

    [Fact]
    public void RatingDatasetSnapshotParsesPngJpgAndJpegFromTheFiveHumanFolders()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            for (var rating = 1; rating <= 5; rating++)
            {
                var source = archive.Save(new Generation.FlameGenerator().Generate(rating), BlankFrame(), rating);
                new RatingStore(root).Rate(source.ImagePath, rating);
            }
            File.WriteAllBytes(Path.Combine(root, "ratings", "5", "web-import.jpg"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "ratings", "5", "web-import.jpeg"), [4, 5, 6]);
            Directory.CreateDirectory(Path.Combine(root, "ratings", "not-a-rating"));
            File.WriteAllBytes(Path.Combine(root, "ratings", "not-a-rating", "ignored.png"), [1]);
            var snapshot = PreferenceDatasetBuilder.Snapshot(root);
            Assert.Equal(7, snapshot.Images.Count);
            Assert.Equal(1, snapshot.Statistics.CountFor(1));
            Assert.Equal(3, snapshot.Statistics.CountFor(5));
            Assert.Contains(snapshot.Images, image => image.ImagePath.EndsWith("web-import.jpg", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(snapshot.Images, image => image.ImagePath.EndsWith("web-import.jpeg", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DatasetReadinessUsesExplicitHeuristicColors()
    {
        Assert.Equal(DatasetReadinessColor.Red, DatasetStatistics.FromCounts(new Dictionary<int, int> { [1] = 2 }).Readiness);
        Assert.Equal(DatasetReadinessColor.Amber, DatasetStatistics.FromCounts(Enumerable.Range(1, 5).ToDictionary(rating => rating, _ => 4)).Readiness);
        Assert.Equal(DatasetReadinessColor.Green, DatasetStatistics.FromCounts(Enumerable.Range(1, 5).ToDictionary(rating => rating, _ => 10)).Readiness);
    }

    [Fact]
    public async Task AiScoringProcessesExistingAndNewRenderedFilesWithoutDeletingLowScores()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var first = archive.Save(new Generation.FlameGenerator().Generate(1), BlankFrame(), 1);
            var backend = new FakeBackend(0.12, "model-one");
            await using var service = new ContinuousAiScoringService(backend);
            var ratings = new RatingStore(root);
            await service.InitializeAsync();
            service.Start(root, ratings);
            await WaitUntil(() => service.Status.Completed >= 1);
            Assert.True(File.Exists(Path.Combine(root, "rendered", "012000__" + first.SourceId + ".png")));
            Assert.True(File.Exists(Path.Combine(root, "rendered", "012000__" + first.SourceId + ".flame")));
            Assert.False(File.Exists(first.FlamePath));
            Assert.Single(service.Scores);
            var second = archive.Save(new Generation.FlameGenerator().Generate(2), BlankFrame(), 2);
            await WaitUntil(() => service.Status.Completed >= 2);
            Assert.True(File.Exists(Path.Combine(root, "rendered", "012000__" + second.SourceId + ".png")));
            Assert.True(File.Exists(Path.Combine(root, "rendered", "012000__" + second.SourceId + ".flame")));
            Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(root, "rendered"), "*.png").Count());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RatedFoldersRemainUntouchedWhileAiRescoresCandidates()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var rated = archive.Save(new Generation.FlameGenerator().Generate(3), BlankFrame(), 1);
            var ratings = new RatingStore(root);
            ratings.Rate(rated.ImagePath, 4);
            var before = Directory.EnumerateFiles(Path.Combine(root, "ratings", "4"), "*.png").Select(Path.GetFileName).ToArray();
            var candidate = archive.Save(new Generation.FlameGenerator().Generate(4), BlankFrame(), 2);
            await using var service = new ContinuousAiScoringService(new FakeBackend(0.88, "model-one"));
            await service.InitializeAsync();
            service.Start(root, ratings);
            await WaitUntil(() => service.Status.Completed >= 1);
            Assert.Equal(before, Directory.EnumerateFiles(Path.Combine(root, "ratings", "4"), "*.png").Select(Path.GetFileName).ToArray());
            Assert.True(File.Exists(Path.Combine(root, "ratings", "4", rated.SourceId + ".png")));
            Assert.True(File.Exists(Path.Combine(root, "ratings", "4", rated.SourceId + ".flame")));
            Assert.True(File.Exists(Path.Combine(root, "rendered", "088000__" + candidate.SourceId + ".png")));
            Assert.True(File.Exists(Path.Combine(root, "rendered", "088000__" + candidate.SourceId + ".flame")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RatedDatasetRescoreUpdatesPrefixesAndIncludesPngJpgAndJpegOnlyImages()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var rated = archive.Save(new Generation.FlameGenerator().Generate(31), BlankFrame(), 1);
            var ratings = new RatingStore(root);
            ratings.Rate(rated.ImagePath, 3);
            var ratedImagePath = Path.Combine(root, "ratings", "3", rated.SourceId + ".png");
            var ratedFlamePath = Path.Combine(root, "ratings", "3", rated.SourceId + ".flame");
            var beforeFlame = File.ReadAllText(ratedFlamePath);
            var unpairedImagePath = Path.Combine(root, "ratings", "5", "legacy-style.png");
            BlankFrame().SavePng(unpairedImagePath);
            var unpairedJpegPath = Path.Combine(root, "ratings", "5", "legacy-photo.jpg");
            BlankFrame().SavePng(unpairedJpegPath);

            await using var service = new ContinuousAiScoringService(new FakeBackend(0.74, "model-one"));
            await service.InitializeAsync();
            var count = await service.RescoreRatedAsync(ratings);

            var rescoredImagePath = Path.Combine(root, "ratings", "3", "074000__" + rated.SourceId + ".png");
            var rescoredFlamePath = Path.Combine(root, "ratings", "3", "074000__" + rated.SourceId + ".flame");
            var rescoredUnpairedImagePath = Path.Combine(root, "ratings", "5", "074000__legacy-style.png");
            var rescoredUnpairedJpegPath = Path.Combine(root, "ratings", "5", "074000__legacy-photo.jpg");
            Assert.Equal(3, count);
            Assert.False(File.Exists(ratedImagePath));
            Assert.False(File.Exists(ratedFlamePath));
            Assert.True(File.Exists(rescoredImagePath));
            Assert.True(File.Exists(rescoredFlamePath));
            Assert.True(File.Exists(rescoredUnpairedImagePath));
            Assert.True(File.Exists(rescoredUnpairedJpegPath));
            Assert.Equal(beforeFlame, File.ReadAllText(rescoredFlamePath));
            Assert.Equal(rescoredImagePath, service.Scores[rated.SourceId].ImagePath);
            Assert.Equal(rescoredUnpairedImagePath, service.Scores["legacy-style"].ImagePath);
            Assert.Equal(rescoredUnpairedJpegPath, service.Scores["legacy-photo"].ImagePath);
            Assert.Equal(0.74, service.Scores[rated.SourceId].Score, 6);
            Assert.Equal(3, ratings.FindRating(rescoredImagePath));
            Assert.Equal(5, ratings.FindRating(rescoredUnpairedImagePath));
            Assert.Equal(3, ratings.EnumerateRatedImagePaths().Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StartingAiScoringAgainRescansExistingRenderedFiles()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var candidate = archive.Save(new Generation.FlameGenerator().Generate(32), BlankFrame(), 1);
            var ratings = new RatingStore(root);
            await using var service = new ContinuousAiScoringService(new FakeBackend(0.63, "model-one"));
            await service.InitializeAsync();
            service.Start(root, ratings);
            await WaitUntil(() => service.Status.Completed >= 1);
            await service.StopAsync();

            service.Start(root, ratings);
            await WaitUntil(() => service.Status.Completed >= 1);

            Assert.True(File.Exists(Path.Combine(root, "rendered", "063000__" + candidate.SourceId + ".png")));
            Assert.Equal(candidate.SourceId, service.Scores[candidate.SourceId].SourceId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RetrainingReplacesModelAndRescoresExistingCandidates()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var candidate = archive.Save(new Generation.FlameGenerator().Generate(5), BlankFrame(), 1);
            var backend = new FakeBackend(0.2, "model-one") { NextTrainingVersion = "model-two", ScoreAfterTraining = 0.91 };
            var ratings = new RatingStore(root);
            await using var service = new ContinuousAiScoringService(backend);
            await service.InitializeAsync();
            service.Start(root, ratings);
            await WaitUntil(() => service.Status.Completed >= 1);
            var snapshot = new DatasetSnapshot(root, [], DatasetStatistics.FromCounts(new Dictionary<int, int>()), new DatasetSplit([], [], [], false), DateTimeOffset.UtcNow);
            var result = await service.TrainAsync(snapshot, Path.Combine(root, "models"));
            Assert.Equal("model-two", result.ModelVersion);
            Assert.True(File.Exists(Path.Combine(root, "rendered", "091000__" + candidate.SourceId + ".png")));
            Assert.True(File.Exists(Path.Combine(root, "rendered", "091000__" + candidate.SourceId + ".flame")));
            Assert.Equal("model-two", service.Scores[candidate.SourceId].ModelVersion);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AtomicArchivePublicationLeavesOnlyCompleteCandidateFiles()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var artifact = archive.Save(new Generation.FlameGenerator().Generate(8), BlankFrame(), 1);
            Assert.True(SourceArchive.IsCompleteCandidate(artifact.ImagePath));
            Assert.Single(archive.EnumerateArtifacts());
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CandidateCatalogToleratesLegacyDuplicateSourceIds()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var artifact = archive.Save(new Generation.FlameGenerator().Generate(9), BlankFrame(), 1);
            var duplicatePath = Path.Combine(root, "rendered", "012000__" + artifact.SourceId + ".png");
            File.Copy(artifact.ImagePath, duplicatePath);

            var catalog = new CandidateCatalog();
            catalog.Refresh(archive, new RatingStore(root));

            Assert.Single(catalog.Ordered(false));
            Assert.Equal(artifact.SourceId, catalog.Ordered(false)[0].SourceId);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CandidateCatalogUsesAscendingSourceIdsWithoutAiAndDescendingSourceIdsWithAi()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var generator = new Generation.FlameGenerator();
            archive.Save(generator.Generate(41), BlankFrame(), 1);
            archive.Save(generator.Generate(42), BlankFrame(), 2);
            archive.Save(generator.Generate(43), BlankFrame(), 3);
            var catalog = new CandidateCatalog();
            catalog.Refresh(archive, new RatingStore(root));

            var ascending = catalog.Ordered(false).Select(artifact => artifact.SourceId).ToArray();
            var descending = catalog.Ordered(true).Select(artifact => artifact.SourceId).ToArray();

            Assert.Equal(ascending.OrderBy(sourceId => sourceId, StringComparer.OrdinalIgnoreCase), ascending);
            Assert.Equal(ascending.Reverse(), descending);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CandidateCatalogNextUnseenDoesNotFallBackToAnAlreadySeenCandidate()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var generator = new Generation.FlameGenerator();
            archive.Save(generator.Generate(31), BlankFrame(), 1);
            archive.Save(generator.Generate(32), BlankFrame(), 2);
            archive.Save(generator.Generate(33), BlankFrame(), 3);
            var catalog = new CandidateCatalog();
            var ratings = new RatingStore(root);
            catalog.Refresh(archive, ratings);
            var ordered = catalog.Ordered(false);
            var seen = ordered.Take(2).Select(artifact => artifact.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            ratings.Rate(ordered[1].ImagePath, 1);
            catalog.Refresh(archive, ratings);

            var next = catalog.NextUnseen(ordered[1].SourceId, false, seen);

            Assert.NotNull(next);
            Assert.Equal(ordered[2].SourceId, next!.SourceId);
            Assert.Null(catalog.FirstUnseen(false, ordered.Select(artifact => artifact.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnavailableCudaDiagnosticsNeverClaimAiReady()
    {
        var diagnostics = DeviceDiagnostics.Unavailable("CUDA is unavailable");
        Assert.False(diagnostics.CudaAvailable);
        Assert.False(diagnostics.AiReady);
        Assert.Equal("none", diagnostics.ActiveDevice);
        Assert.Contains("CUDA", diagnostics.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static RenderedFrame BlankFrame() => new(8, 8, Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray());

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition(), "Timed out waiting for the asynchronous scoring service.");
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffc-phase-two-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeBackend : IPreferenceScoringBackend
    {
        private double _score;
        private string _version;

        public FakeBackend(double score, string version) { _score = score; _version = version; }
        public string? NextTrainingVersion { get; set; }
        public double ScoreAfterTraining { get; set; }
        public DeviceDiagnostics Diagnostics { get; private set; } = new(true, true, true, "Test GPU", "test", "cuda:0", true, "test backend");
        public string? ActiveModelVersion => _version;
        public Task<DeviceDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken) => Task.FromResult(Diagnostics);

        public Task<IReadOnlyList<PreferenceScore>> ScoreAsync(IReadOnlyList<string> imagePaths, CancellationToken cancellationToken)
        {
            var scores = imagePaths.Select(path => new PreferenceScore(path, CandidateFileNaming.GetSourceId(Path.GetFileName(path)), _score, 1 + _score * 4, _version)).ToArray();
            return Task.FromResult<IReadOnlyList<PreferenceScore>>(scores);
        }

        public Task<TrainingResult> TrainAsync(DatasetSnapshot snapshot, string modelDirectory, CancellationToken cancellationToken)
        {
            _version = NextTrainingVersion ?? _version;
            _score = ScoreAfterTraining == 0 ? _score : ScoreAfterTraining;
            return Task.FromResult(new TrainingResult(_version, snapshot.Statistics, snapshot.Split, new TrainingMetrics(0, 0, 0, 0, 0, false, [])));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
