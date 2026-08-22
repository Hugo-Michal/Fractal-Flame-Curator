using System.Text;
using System.Xml.Linq;
using FractalFlameCurator.Generation;
using FractalFlameCurator.Models;
using FractalFlameCurator.Pipeline;
using FractalFlameCurator.Rendering;
using FractalFlameCurator.Serialization;
using FractalFlameCurator.Storage;
using Xunit;

namespace FractalFlameCurator.Tests;

public sealed class PhaseOneTests
{
    [Fact]
    public void GeneratedFlameIsValidApophysisXml()
    {
        var genome = new FlameGenerator().Generate(12345);
        var xml = FlameXmlSerializer.Serialize(genome);
        var document = XDocument.Parse(xml);
        Assert.Equal("flames", document.Root?.Name.LocalName);
        Assert.NotNull(document.Root?.Element("flame"));
        Assert.Equal(256, document.Root!.Element("flame")!.Element("palette")!.Attribute("count")!.Value is "256" ? 256 : 0);
        var roundTrip = FlameXmlSerializer.Deserialize(xml);
        Assert.Equal(genome.Seed, roundTrip.Seed);
        Assert.Equal(genome.Transforms.Count, roundTrip.Transforms.Count);
        Assert.Equal(genome.Transforms.SelectMany(t => t.Variations.Keys).OrderBy(x => x), roundTrip.Transforms.SelectMany(t => t.Variations.Keys).OrderBy(x => x));
    }

    [Fact]
    public void SavedFlameUsesApophysisFileEncodingAndAttributeNames()
    {
        var root = NewTempDirectory();
        try
        {
            var genome = new FlameGenerator().Generate(12345);
            var path = Path.Combine(root, "candidate.flame");
            FlameXmlSerializer.Save(genome, path);

            var bytes = File.ReadAllBytes(path);
            Assert.NotEmpty(bytes);
            Assert.Equal((byte)'<', bytes[0]);
            Assert.StartsWith("<flames>", File.ReadAllText(path, Encoding.UTF8));

            var document = XDocument.Load(path);
            var flame = document.Root!.Element("flame")!;
            Assert.Equal("Apophysis 7X", flame.Attribute("version")!.Value);
            Assert.Equal("4.76837158203125", flame.Attribute("quality")!.Value);
            var transforms = flame.Elements("xform").ToArray();
            Assert.NotEmpty(transforms);
            Assert.All(transforms, transform =>
            {
                Assert.NotNull(transform.Attribute("coefs"));
                Assert.Null(transform.Attribute("a"));
                Assert.DoesNotContain(transform.Attributes(), attribute => attribute.Name.LocalName.StartsWith("var_", StringComparison.OrdinalIgnoreCase));
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeserializerStillReadsPreviouslyGeneratedLegacyAttributes()
    {
        var genome = new FlameGenerator().Generate(6789);
        var document = XDocument.Parse(FlameXmlSerializer.Serialize(genome));
        foreach (var transform in document.Root!.Element("flame")!.Elements("xform"))
        {
            var coefs = transform.Attribute("coefs")!.Value.Split(' ');
            transform.Attribute("coefs")!.Remove();
            var coefficientNames = new[] { "a", "b", "c", "d", "e", "f" };
            for (var i = 0; i < coefficientNames.Length; i++) transform.SetAttributeValue(coefficientNames[i], coefs[i]);
            foreach (var variation in transform.Attributes().Where(attribute => VariationRegistry.Names.Contains(attribute.Name.LocalName)).ToArray())
            {
                var value = variation.Value;
                variation.Remove();
                transform.SetAttributeValue("var_" + variation.Name.LocalName, value);
            }
        }

        var roundTrip = FlameXmlSerializer.Deserialize(document.ToString(SaveOptions.DisableFormatting));
        Assert.Equal(genome.Transforms.Select(transform => transform.A).ToArray(), roundTrip.Transforms.Select(transform => transform.A).ToArray());
        Assert.Equal(genome.Transforms.SelectMany(transform => transform.Variations.Keys).OrderBy(name => name), roundTrip.Transforms.SelectMany(transform => transform.Variations.Keys).OrderBy(name => name));
    }

    [Fact]
    public async Task FixedSeedProducesTheSameGenomeAndPixels()
    {
        var first = new FlameGenerator().Generate(777);
        var second = new FlameGenerator().Generate(777);
        Assert.Equal(FlameXmlSerializer.Serialize(first), FlameXmlSerializer.Serialize(second));
        var renderer = new CpuFlameRenderer();
        var settings = new RenderSettings { Width = 64, Height = 64, SampleBudget = 7_000, Oversample = 1, FilterRadius = 0.5, PaletteName = "Monochrome" };
        var firstFrame = await renderer.RenderAsync(first, settings, null, CancellationToken.None);
        var secondFrame = await renderer.RenderAsync(second, settings, null, CancellationToken.None);
        Assert.Equal(firstFrame.BgraPixels, secondFrame.BgraPixels);
    }

    [Fact]
    public async Task DefaultRenderIsSquare2048AndMonochrome()
    {
        var genome = new FlameGenerator().Generate(314159);
        var settings = new RenderSettings { SampleBudget = 1_000 };
        Assert.Equal(20_000_000, new RenderSettings().SampleBudget);
        var frame = await new CpuFlameRenderer().RenderAsync(genome, settings, null, CancellationToken.None);
        Assert.Equal(2048, frame.Width);
        Assert.Equal(2048, frame.Height);
        Assert.Equal(2048 * 2048 * 4, frame.BgraPixels.Length);
        Assert.Equal(PaletteDefinition.Monochrome.Name, genome.Palette.Name);
        Assert.Equal(255, frame.BgraPixels[0]);
        Assert.Equal(255, frame.BgraPixels[1]);
        Assert.Equal(255, frame.BgraPixels[2]);
    }

    [Fact]
    public void GeneratorAuditsBroadVariationCoverage()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generator = new FlameGenerator();
        for (var seed = 1; seed <= 300; seed++)
        {
            seen.UnionWith(generator.Generate(seed).Transforms.SelectMany(transform => transform.Variations.Keys));
        }
        Assert.True(seen.Count >= 30, $"Only {seen.Count} variations were sampled.");
        Assert.All(seen, name => Assert.Contains(name, VariationRegistry.Names));
    }

    [Fact]
    public void GeneratorSettingsControlTheSearchSpaceAndUseFixedCameraDefaults()
    {
        var settings = StableGeneratorSettings() with
        {
            MinimumTransformCount = 3,
            MaximumTransformCount = 3,
            AllowFinalTransforms = true,
            FinalTransformChance = 1
        };

        var genome = new FlameGenerator().Generate(4242, new FlameGeneratorOptions { GeneratorSettings = settings });

        Assert.Equal(3, genome.Transforms.Count);
        Assert.Equal(0, genome.CenterX);
        Assert.Equal(0, genome.CenterY);
        Assert.Equal(100, genome.Scale);
        Assert.Equal(0, genome.Rotate);
        Assert.Equal(1, genome.Brightness);
        Assert.Equal(1, genome.Gamma);
        Assert.Equal(1, genome.Vibrancy);
        Assert.All(genome.Transforms, transform =>
        {
            Assert.Equal(1, transform.Weight);
            Assert.Equal(0.5, transform.A, 10);
            Assert.Equal(0, transform.B, 10);
            Assert.Equal(0, transform.C, 10);
            Assert.Equal(0.5, transform.D, 10);
            Assert.Equal(0, transform.E, 10);
            Assert.Equal(0, transform.F, 10);
            Assert.Equal(0.3, transform.Symmetry, 10);
            Assert.Equal(1, transform.Variations["linear"], 10);
            Assert.Null(transform.PostTransform);
        });
        Assert.NotNull(genome.FinalTransform);
        Assert.Equal(0, genome.FinalTransform!.Weight);
        Assert.Equal(1, genome.FinalTransform.Variations["linear"], 10);
    }

    [Fact]
    public void GeneratedVariationWeightsAreNormalized()
    {
        var settings = StableGeneratorSettings() with
        {
            MinimumVariationCount = 3,
            MaximumVariationCount = 3,
            EnabledVariations = ["linear", "sinusoidal", "spherical"],
            VariationBlendDominance = 0.8
        };

        var genome = new FlameGenerator().Generate(781, new FlameGeneratorOptions { GeneratorSettings = settings });

        Assert.All(genome.Transforms, transform => Assert.Equal(1, transform.Variations.Values.Sum(), 10));
    }

    [Fact]
    public void AllowedSymmetryTypesUseFixedChanceAndOrders()
    {
        var reflectionSettings = StableGeneratorSettings() with { SymmetryTypes = AllowedSymmetryTypes.Reflection };
        var reflectionValues = Enumerable.Range(1, 100)
            .Select(seed => new FlameGenerator().Generate(seed, new FlameGeneratorOptions { GeneratorSettings = reflectionSettings }).Symmetry)
            .ToArray();
        Assert.Contains(-1, reflectionValues);
        Assert.All(reflectionValues, symmetry => Assert.Contains(symmetry, new[] { 1, -1 }));

        var dihedralSettings = reflectionSettings with { SymmetryTypes = AllowedSymmetryTypes.Dihedral };
        var dihedralValues = Enumerable.Range(1, 100)
            .Select(seed => new FlameGenerator().Generate(seed, new FlameGeneratorOptions { GeneratorSettings = dihedralSettings }).Symmetry)
            .ToArray();
        Assert.Contains(dihedralValues, symmetry => symmetry is -2 or -3);
        Assert.All(dihedralValues, symmetry => Assert.Contains(symmetry, new[] { 1, -2, -3 }));
    }

    [Fact]
    public async Task RendererAppliesReflectionSymmetry()
    {
        var settings = StableGeneratorSettings() with
        {
            SymmetryTypes = AllowedSymmetryTypes.Reflection,
            TranslationExtent = 0.75
        };
        var reflected = Enumerable.Range(1, 100)
            .Select(seed => new FlameGenerator().Generate(seed, new FlameGeneratorOptions { GeneratorSettings = settings }))
            .First(genome => genome.Symmetry == -1);
        var unsymmetrical = reflected.Clone();
        unsymmetrical.Symmetry = 1;
        var renderSettings = new RenderSettings { Width = 64, Height = 64, SampleBudget = 20_000, Gamma = 1 };
        var renderer = new CpuFlameRenderer();

        var reflectedFrame = await renderer.RenderAsync(reflected, renderSettings, null, CancellationToken.None);
        var unsymmetricalFrame = await renderer.RenderAsync(unsymmetrical, renderSettings, null, CancellationToken.None);

        Assert.False(reflectedFrame.BgraPixels.SequenceEqual(unsymmetricalFrame.BgraPixels));
    }

    [Fact]
    public async Task RendererCancelsWithoutReturningAFrame()
    {
        var genome = new FlameGenerator().Generate(22);
        var renderer = new CpuFlameRenderer();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(5);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(genome, new RenderSettings { Width = 128, Height = 128, SampleBudget = 20_000_000 }, null, cancellation.Token));
    }

    [Fact]
    public async Task RendererUsesTheConfiguredSampleBudget()
    {
        var progress = new RecordingProgress();
        var genome = new FlameGenerator().Generate(123);
        await new CpuFlameRenderer().RenderAsync(genome, new RenderSettings { Width = 64, Height = 64, SampleBudget = 12_345 }, progress, CancellationToken.None);
        Assert.Equal(12_345, progress.Last.CompletedSamples);
        Assert.Equal(12_345, progress.Last.TotalSamples);
    }

    [Fact]
    public void ToneMappingUsesDensityRangeAndLowDensityCutoff()
    {
        var counts = new[] { 0d, 1d, 4d, 16d };
        var channels = new double[counts.Length];
        var pixels = ToneMapper.Map(counts, channels, channels, channels, new RenderSettings
        {
            Gamma = 1,
            Brightness = 1,
            WhitePoint = 0,
            BlackPoint = 0.75,
            ContrastCurve = 1,
            LowDensityCutoff = 0.3
        }, monochrome: true);

        Assert.Equal(255, pixels[0]);
        Assert.Equal(255, pixels[4]);
        Assert.InRange(pixels[8], (byte)1, (byte)254);
        Assert.Equal(0, pixels[12]);
    }

    [Fact]
    public void HigherContrastCurveSeparatesTheMiddleTone()
    {
        var counts = new[] { 0d, 4d, 16d };
        var channels = new double[counts.Length];
        var neutral = ToneMapper.Map(counts, channels, channels, channels, new RenderSettings { Gamma = 1, ContrastCurve = 1 }, true);
        var highContrast = ToneMapper.Map(counts, channels, channels, channels, new RenderSettings { Gamma = 1, ContrastCurve = 2 }, true);

        Assert.True(highContrast[4] < neutral[4]);
    }

    [Fact]
    public void BackendReportingDoesNotClaimGpu()
    {
        var status = new CpuFlameRenderer().Status;
        Assert.Equal("CPU", status.Backend);
        Assert.False(status.IsGpu);
        Assert.Contains("CPU", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BoundedQueueNeverExceedsCapacity()
    {
        var queue = new BoundedRenderQueue<int>(1);
        await queue.EnqueueAsync(1, CancellationToken.None);
        var pending = queue.EnqueueAsync(2, CancellationToken.None).AsTask();
        await Task.Delay(30);
        Assert.Equal(1, queue.Count);
        Assert.False(pending.IsCompleted);
        Assert.Equal(1, await queue.DequeueAsync(CancellationToken.None));
        await pending;
        Assert.Equal(1, queue.Count);
        Assert.Equal(2, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RendererFailureIsCountedAndSessionKeepsItsBoundary()
    {
        var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new ContinuousRenderService(new FlameGenerator(), new ThrowingRenderer());
        service.RenderFailed += _ => failed.TrySetResult(true);
        service.Start(new ContinuousRenderOptions { OutputDirectory = NewTempDirectory(), BatchLimit = 1, QueueCapacity = 1, WorkerCount = 1 });
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, service.Status.Failed);
        await service.StopAsync();
    }

    [Fact]
    public async Task FiniteSessionStopsReportingRunningAfterWorkersDrain()
    {
        var root = NewTempDirectory();
        try
        {
            await using var service = new ContinuousRenderService(new FlameGenerator(), new FastRenderer());
            service.Start(new ContinuousRenderOptions { OutputDirectory = root, BatchLimit = 2, QueueCapacity = 1, WorkerCount = 1 });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (service.Status.Completed < 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(2, service.Status.Completed);
            Assert.False(service.Status.IsRunning);
            Assert.Equal(2, service.Status.ReadyCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RatingMovesPngAndFlameTogetherAndUndoRestoresThePair()
    {
        var root = NewTempDirectory();
        try
        {
            var genome = new FlameGenerator().Generate(98);
            var archive = new SourceArchive(root);
            var frame = new RenderedFrame(32, 32, new byte[32 * 32 * 4]);
            var artifact = archive.Save(genome, frame, 1);
            var ratings = new RatingStore(root);
            ratings.Rate(artifact.ImagePath, 2);
            var ratingTwoImage = Path.Combine(root, "ratings", "2", artifact.SourceId + ".png");
            var ratingTwoFlame = Path.Combine(root, "ratings", "2", artifact.SourceId + ".flame");
            Assert.False(File.Exists(artifact.ImagePath));
            Assert.False(File.Exists(artifact.FlamePath));
            Assert.True(File.Exists(ratingTwoImage));
            Assert.True(File.Exists(ratingTwoFlame));
            Assert.Equal(2, ratings.FindRating(artifact.ImagePath));
            var ratedArtifacts = ratings.EnumerateRatedArtifacts();
            Assert.Single(ratedArtifacts);
            Assert.Equal(ratingTwoImage, ratedArtifacts[0].ImagePath);
            Assert.Equal(ratingTwoFlame, ratedArtifacts[0].FlamePath);
            Assert.True(ratings.RatingFoldersContainPairedFiles());
            ratings.Rate(ratingTwoImage, 5);
            Assert.Equal(5, ratings.FindRating(artifact.ImagePath));
            Assert.False(File.Exists(ratingTwoImage));
            Assert.False(File.Exists(ratingTwoFlame));
            Assert.True(ratings.Undo());
            Assert.Equal(2, ratings.FindRating(artifact.ImagePath));
            Assert.True(File.Exists(ratingTwoImage));
            Assert.True(File.Exists(ratingTwoFlame));
            Assert.True(ratings.Undo());
            Assert.Null(ratings.FindRating(artifact.ImagePath));
            Assert.True(File.Exists(artifact.ImagePath));
            Assert.True(File.Exists(artifact.FlamePath));
            Assert.True(ratings.RatingFoldersContainPairedFiles());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RerenderReplacesOnlyTheImageAndPreservesTheSourceFlame()
    {
        var root = NewTempDirectory();
        try
        {
            var genome = new FlameGenerator().Generate(901);
            var archive = new SourceArchive(root);
            var artifact = archive.Save(genome, new RenderedFrame(4, 4, Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray()), 1);
            var originalFlame = File.ReadAllText(artifact.FlamePath);
            var originalImage = File.ReadAllBytes(artifact.ImagePath);

            await new ArtifactRerenderer(new ReplacementRenderer()).RerenderAsync(
                artifact,
                PaletteDefinition.Fire,
                new RenderSettings { Width = 4, Height = 4, SampleBudget = 100 },
                null,
                CancellationToken.None);

            Assert.Equal(originalFlame, File.ReadAllText(artifact.FlamePath));
            Assert.False(originalImage.SequenceEqual(File.ReadAllBytes(artifact.ImagePath)));
            Assert.True(SourceArchive.IsCompleteCandidate(artifact.ImagePath));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ffc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static RandomGeneratorSettings StableGeneratorSettings() => new()
    {
        MinimumTransformCount = 2,
        MaximumTransformCount = 2,
        SymmetryTypes = AllowedSymmetryTypes.None,
        MinimumAffineRotationDegrees = 0,
        MaximumAffineRotationDegrees = 0,
        MinimumAffineScale = 0.5,
        MaximumAffineScale = 0.5,
        MinimumAffineShear = 0,
        MaximumAffineShear = 0,
        TranslationExtent = 0,
        TransformSelectionBalance = 0,
        MinimumVariationCount = 1,
        MaximumVariationCount = 1,
        EnabledVariations = ["linear"],
        VariationBlendDominance = 0,
        PostTransformChance = 0,
        AllowFinalTransforms = false
    };

    private sealed class ThrowingRenderer : IFlameRenderer
    {
        public RendererStatus Status { get; } = new("CPU", "Test CPU", false, "Test renderer");
        public Task<RenderedFrame> RenderAsync(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("synthetic renderer failure");
    }

    private sealed class FastRenderer : IFlameRenderer
    {
        public RendererStatus Status { get; } = new("CPU", "Test CPU", false, "Test renderer");
        public Task<RenderedFrame> RenderAsync(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(new RenderedFrame(32, 32, new byte[32 * 32 * 4]));
    }

    private sealed class ReplacementRenderer : IFlameRenderer
    {
        public RendererStatus Status { get; } = new("CPU", "Test CPU", false, "Test renderer");
        public Task<RenderedFrame> RenderAsync(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
            => Task.FromResult(new RenderedFrame(4, 4, Enumerable.Repeat((byte)0, 4 * 4 * 4).ToArray()));
    }

    private sealed class RecordingProgress : IProgress<RenderProgress>
    {
        public RenderProgress Last { get; private set; } = new(0, 0);
        public void Report(RenderProgress value) => Last = value;
    }
}
