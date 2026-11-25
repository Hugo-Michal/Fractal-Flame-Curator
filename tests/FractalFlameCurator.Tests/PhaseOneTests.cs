using System.Text;
using System.Xml.Linq;
using FractalFlameCurator.Generation;
using FractalFlameCurator.Controls;
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
    public void WorkspacePreferenceStoreRestoresTheSelectedWorkspace()
    {
        var root = NewTempDirectory();
        try
        {
            var preferencesDirectory = Path.Combine(root, "preferences");
            var workspace = Path.Combine(root, "workspace");
            var store = new WorkspacePreferenceStore(preferencesDirectory);

            Assert.Null(store.Load());
            store.Save(workspace);

            Assert.Equal(Path.GetFullPath(workspace), new WorkspacePreferenceStore(preferencesDirectory).Load());
        }
        finally { Directory.Delete(root, true); }
    }

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

    [Theory]
    [InlineData(20_000_000_000L, 10_000)]
    [InlineData(1_000_000_000_000L, 10_000)]
    [InlineData(999_999_999_999L, 5600)]
    public void LargeSampleBudgetsRoundTripInTheFlameFormat(long budget, int size)
    {
        var genome = new FlameGenerator().Generate(12345);
        genome.Quality = budget;
        genome.Width = genome.Height = size;
        var roundTrip = FlameXmlSerializer.Deserialize(FlameXmlSerializer.Serialize(genome));
        Assert.Equal(budget, roundTrip.Quality);
        Assert.Equal(size, roundTrip.Width);
        Assert.Equal(size, roundTrip.Height);
    }

    [Fact]
    public void GroupedIntegerInputFormatsAndParsesLargeValues()
    {
        Assert.Equal("1 000 000 000 000", GroupedIntegerInput.Format(RenderSettings.MaximumSampleBudget));
        Assert.Equal(RenderSettings.MaximumSampleBudget, GroupedIntegerInput.Parse("1 000 000 000 000", "Sample budget", 100, RenderSettings.MaximumSampleBudget));
        Assert.Equal(10_000L, GroupedIntegerInput.Parse("10000", "Output size", 32, 10_000));
        Assert.False(GroupedIntegerInput.TryParse("20,000,000,000", out _));
        Assert.Throws<InvalidOperationException>(() => GroupedIntegerInput.Parse("1 000 000 000 001", "Sample budget", 100, RenderSettings.MaximumSampleBudget));
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
    public async Task ParallelRendererIsDeterministicForTheSameWorkerCountAndSupportsSixTimesOversampling()
    {
        var genome = new FlameGenerator().Generate(777);
        genome.Palette = PaletteDefinition.Fire;
        genome.Oversample = 6;
        var settings = new RenderSettings { Width = 64, Height = 64, SampleBudget = 40_000, Oversample = 6, WorkerCount = 4, Gamma = 1 };
        var renderer = new CpuFlameRenderer();
        var first = await renderer.RenderAsync(genome, settings, null, CancellationToken.None);
        var second = await renderer.RenderAsync(genome, settings, null, CancellationToken.None);
        var triple = await renderer.RenderAsync(genome, settings with { Oversample = 3 }, null, CancellationToken.None);
        Assert.Equal(first.BgraPixels, second.BgraPixels);
        Assert.False(first.BgraPixels.SequenceEqual(triple.BgraPixels));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SampleThenMapMatchesTheExistingRender(bool color)
    {
        var genome = new FlameGenerator().Generate(777);
        if (color) genome.Palette = PaletteDefinition.Fire;
        var settings = new RenderSettings { Width = 64, Height = 64, SampleBudget = 30_000, Oversample = 2, WorkerCount = 4, Gamma = 1 };
        var renderer = new CpuFlameRenderer();
        var direct = await renderer.RenderAsync(genome, settings, null, CancellationToken.None);
        var histogram = await renderer.SampleAsync(genome, settings, null, CancellationToken.None);
        var mapped = await renderer.MapAsync(histogram, settings, CancellationToken.None);
        Assert.Equal(direct.BgraPixels, mapped.BgraPixels);
    }

    [Fact]
    public async Task RenderSessionReusesSamplesForAdjustmentsAndPreservesThemOnCancellation()
    {
        var genome = new FlameGenerator().Generate(123);
        var settings = new RenderSettings { Width = 64, Height = 64, SampleBudget = 50_000, Gamma = 1, BlackPoint = 0.85 };
        var renderer = new CountingHistogramRenderer();
        var session = new FlameRenderSession(renderer);
        Assert.Equal(FlameRenderChange.Sampling, session.GetChange(genome, settings, 0));
        async Task<RenderedFrame16> Apply(RenderSettings values)
        {
            var prepared = await session.PrepareAsync(genome, values, 0, null, CancellationToken.None);
            session.Commit(prepared);
            return prepared.Frame;
        }
        var original = await Apply(settings);
        Assert.Equal(FlameRenderChange.None, session.GetChange(genome, settings, 0));
        Assert.True(session.IsInputCurrent(genome, settings, 0));
        Assert.False(session.IsInputCurrent(genome, settings with { FilterRadius = 1 }, 0));
        Assert.False(session.IsInputCurrent(genome, settings, 1));

        var adjusted = settings with { Gamma = 0.6 };
        Assert.Equal(FlameRenderChange.Adjustment, session.GetChange(genome, adjusted, 0));
        var brighter = await Apply(adjusted);
        Assert.False(original.Pixels.SequenceEqual(brighter.Pixels));
        var restored = await Apply(settings);
        Assert.Equal(original.Pixels, restored.Pixels);
        Assert.Equal(1, renderer.Samples);
        Assert.Equal(3, renderer.Maps);

        foreach (var values in new[]
        {
            settings with { Brightness = 0.8 }, settings with { Vibrancy = 0.5 },
            settings with { WhitePoint = 0.1 }, settings with { BlackPoint = 0.9 },
            settings with { ContrastCurve = 1.2 }, settings with { LowDensityCutoff = 0.02 }
        }) Assert.Equal(FlameRenderChange.Adjustment, session.GetChange(genome, values, 0));
        Assert.Equal(FlameRenderChange.Adjustment, session.GetChange(genome, settings with { FilterRadius = 1 }, 0));
        Assert.Equal(FlameRenderChange.Adjustment, session.GetChange(genome, settings, 1));
        Assert.Equal(FlameRenderChange.Adjustment, session.GetChange(genome, settings with { NormalizeDensityBySampleCount = true }, 0));
        Assert.Equal(FlameRenderChange.Sampling, session.GetChange(genome, settings with { SampleBudget = 60_000 }, 0));
        Assert.Equal(FlameRenderChange.Sampling, session.GetChange(genome, settings with { Oversample = 2 }, 0));
        Assert.Equal(FlameRenderChange.Sampling, session.GetChange(genome, settings with { Width = 128, Height = 128 }, 0));
        Assert.Equal(FlameRenderChange.Sampling, session.GetChange(genome, settings with { WorkerCount = 2 }, 0));
        var changedCamera = genome.Clone();
        changedCamera.CenterX = 0.25;
        Assert.Equal(FlameRenderChange.Sampling, session.GetChange(changedCamera, settings, 0));
        var changedPalette = genome.Clone();
        changedPalette.Palette = PaletteDefinition.Fire;
        Assert.Equal(FlameRenderChange.Sampling, session.GetChange(changedPalette, settings, 0));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrepareAsync(genome, adjusted, 0, null, cancellation.Token));
        Assert.Equal(FlameRenderChange.None, session.GetChange(genome, settings, 0));
        Assert.Same(restored, (await session.PrepareAsync(genome, settings, 0, null, CancellationToken.None)).Frame);

        var newSamples = settings with { SampleBudget = 60_000 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrepareAsync(genome, newSamples, 0, null, cancellation.Token));
        Assert.Equal(FlameRenderChange.None, session.GetChange(genome, settings, 0));
        renderer.FailNextMap = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.PrepareAsync(genome, newSamples, 0, null, CancellationToken.None));
        Assert.Equal(FlameRenderChange.Adjustment, session.GetChange(genome, newSamples, 0));
        await Apply(newSamples);
        Assert.Equal(2, renderer.Samples);
        session.Clear();
        Assert.Equal(FlameRenderChange.Sampling, session.GetChange(genome, settings, 0));
    }

    [Fact]
    public async Task MasterCurveChangesReuseSamplesAndCanRestoreTheOriginalPixels()
    {
        var genome = new FlameGenerator().Generate(123);
        var settings = new RenderSettings { Width = 64, Height = 64, SampleBudget = 50_000, Gamma = 1 };
        var renderer = new CountingHistogramRenderer();
        var session = new FlameRenderSession(renderer);
        var original = await session.PrepareAsync(genome, settings, 0, null, CancellationToken.None);
        session.Commit(original);
        Assert.Equal(64 * 64, original.Frame.Pixels.Length);
        Assert.Equal(64 * 64, original.ColorHistogram.Red.Sum());
        Assert.Equal(FlameRenderChange.None, session.GetChange(genome, settings, 0, new ToneCurve(ToneCurve.Identity.Points)));
        var curve = new ToneCurve([new(0, 0), new(0.5, 0.8), new(1, 1)]);
        Assert.Equal(FlameRenderChange.Adjustment, session.GetChange(genome, settings, 0, curve));
        Assert.True(session.IsInputCurrent(genome, settings, 0));
        var edited = await session.PrepareAsync(genome, settings, 0, null, CancellationToken.None, curve);
        session.Commit(edited);
        Assert.False(original.Frame.Pixels.SequenceEqual(edited.Frame.Pixels));
        Assert.Equal(original.ColorHistogram.Red, edited.ColorHistogram.Red);
        Assert.Equal(original.ColorHistogram.Green, edited.ColorHistogram.Green);
        Assert.Equal(original.ColorHistogram.Blue, edited.ColorHistogram.Blue);
        Assert.False(original.Frame.ColorHistogram!.Red.SequenceEqual(edited.Frame.ColorHistogram!.Red));
        Assert.Equal(64 * 64, edited.Frame.ColorHistogram.Red.Sum());
        var restored = await session.PrepareAsync(genome, settings, 0, null, CancellationToken.None);
        session.Commit(restored);
        Assert.Equal(original.Frame.Pixels, restored.Frame.Pixels);
        Assert.Equal(original.Frame.ColorHistogram.Red, restored.Frame.ColorHistogram!.Red);
        var smoothed = await session.PrepareAsync(genome, settings, 1, null, CancellationToken.None, curve);
        Assert.False(original.ColorHistogram.Red.SequenceEqual(smoothed.ColorHistogram.Red));
        Assert.Equal(1, renderer.Samples);
        Assert.Equal(4, renderer.Maps);
    }

    [Fact]
    public async Task AutomaticCurveFitsEachInputAndReusesSampledPoints()
    {
        var genome = new FlameGenerator().Generate(123);
        var settings = new RenderSettings { Width = 64, Height = 64, SampleBudget = 50_000, Gamma = 1 };
        var renderer = new CountingHistogramRenderer();
        var session = new FlameRenderSession(renderer);
        var original = await session.PrepareAsync(genome, settings, 0, null, CancellationToken.None);
        session.Commit(original);
        Assert.Equal(FlameRenderChange.Adjustment, session.GetChange(genome, settings, 0, null, automaticCurve: true));
        var fitted = await session.PrepareAsync(genome, settings, 0, null, CancellationToken.None, null, automaticCurve: true);
        session.Commit(fitted);
        Assert.Equal((ushort)0, fitted.Frame.Pixels.Min());
        Assert.Equal(ushort.MaxValue, fitted.Frame.Pixels.Max());
        Assert.Equal(original.ColorHistogram.Red, fitted.ColorHistogram.Red);
        Assert.Equal(1, renderer.Samples);
        Assert.Equal(2, renderer.Maps);
        var unusedManualCurve = new ToneCurve([new(0, 0), new(0.5, 0.8), new(1, 1)]);
        Assert.Equal(FlameRenderChange.None, session.GetChange(genome, settings, 0, unusedManualCurve, automaticCurve: true));
        Assert.Equal(FlameRenderChange.Adjustment, session.GetChange(genome, settings, 0, unusedManualCurve));

        var flat = new HighPrecisionFrame(2, 1, 1, [0.4f, 0.4f]);
        Assert.Equal(ToneCurve.Identity, PrecisionFrameProcessor.CreateAutomaticCurve(flat, CancellationToken.None));
        var another = new HighPrecisionFrame(2, 1, 1, [0.2f, 0.6f]);
        var anotherCurve = PrecisionFrameProcessor.CreateAutomaticCurve(another, CancellationToken.None);
        Assert.Equal((double)another.Pixels[0], anotherCurve.Points[0].X);
        Assert.Equal((double)another.Pixels[1], anotherCurve.Points[^1].X);
        Assert.Equal(new ushort[] { 0, 65535 }, PrecisionFrameProcessor.ApplyCurve(another, anotherCurve, CancellationToken.None).Frame.Pixels);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => PrecisionFrameProcessor.CreateAutomaticCurve(another, cancellation.Token));
    }

    [Fact]
    public async Task BatchRendersInOrderWithSharedSettingsAndPreservesExistingFiles()
    {
        var root = NewTempDirectory();
        try
        {
            var first = new FlameGenerator().Generate(123);
            var second = new FlameGenerator().Generate(456);
            second.Palette = PaletteDefinition.Fire;
            var firstPath = Path.Combine(root, "first.flame");
            var secondPath = Path.Combine(root, "second.flame");
            FlameXmlSerializer.Save(first, firstPath);
            FlameXmlSerializer.Save(second, secondPath);
            var firstBefore = File.ReadAllBytes(firstPath);
            var secondBefore = File.ReadAllBytes(secondPath);
            var outputFolder = Path.Combine(root, "rerendered"); Directory.CreateDirectory(outputFolder);
            var existing = Path.Combine(outputFolder, "first_rerender.png"); File.WriteAllText(existing, "existing image");
            var renderer = new CountingHistogramRenderer();
            var session = new FlameRenderSession(renderer);
            var batch = new FlameBatchRerenderer(session);
            var settings = new RenderSettings { Width = 32, Height = 32, SampleBudget = 5_000, Gamma = 1, WorkerCount = 1 };
            first.CenterX = 0.125; first.Scale = 120;
            var completed = new List<string>();
            var results = await batch.RenderAsync([secondPath, firstPath], first, settings, 0, ToneCurve.Identity, true, false,
                (_, _, _) => { }, null, (path, genome, prepared, output) =>
                {
                    completed.Add(path);
                    Assert.Equal(0.125, genome.CenterX);
                    Assert.Equal(120, genome.Scale);
                    Assert.Equal(settings.SampleBudget, genome.Quality);
                    Assert.Equal(PaletteDefinition.Monochrome.Name, genome.Palette.Name);
                    Assert.Equal((ushort)0, prepared.Frame.Pixels.Min());
                    Assert.Equal(ushort.MaxValue, prepared.Frame.Pixels.Max());
                    Assert.True(File.Exists(output));
                    return Task.CompletedTask;
                }, CancellationToken.None);
            Assert.Equal(new[] { secondPath, firstPath }, completed);
            Assert.Equal(new[] { secondPath, firstPath }, results.Select(result => result.SourcePath));
            Assert.All(results, result => Assert.Null(result.Error));
            Assert.Equal(Path.Combine(outputFolder, "second_rerender.png"), results[0].OutputPath);
            Assert.Equal(Path.Combine(outputFolder, "first_rerender_002.png"), results[1].OutputPath);
            Assert.Equal("existing image", File.ReadAllText(existing));
            Assert.Equal(firstBefore, File.ReadAllBytes(firstPath));
            Assert.Equal(secondBefore, File.ReadAllBytes(secondPath));
            Assert.Equal(2, renderer.Samples);
            Assert.All(results, result => Assert.Equal((byte)16, File.ReadAllBytes(result.OutputPath!)[24]));

            var sourcePalettes = new List<PaletteDefinition>();
            var sourceResults = await batch.RenderAsync([firstPath, secondPath], first, settings, 0, ToneCurve.Identity, true, true,
                (_, _, _) => { }, null, (_, genome, _, _) => { sourcePalettes.Add(genome.Palette); return Task.CompletedTask; }, CancellationToken.None);
            Assert.Equal(2, sourcePalettes.Count);
            Assert.Equal(PaletteDefinition.Monochrome.Colors, sourcePalettes[0].Colors);
            Assert.Equal(PaletteDefinition.Fire.Colors, sourcePalettes[1].Colors);
            Assert.All(sourceResults, result => Assert.Null(result.Error));
            Assert.Equal((byte)0, File.ReadAllBytes(sourceResults[0].OutputPath!)[25]);
            Assert.Equal((byte)2, File.ReadAllBytes(sourceResults[1].OutputPath!)[25]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BatchContinuesAfterAnInvalidSourceAndCancellationKeepsCompletedImages()
    {
        var root = NewTempDirectory();
        try
        {
            var genome = new FlameGenerator().Generate(321);
            var first = Path.Combine(root, "first.flame"); var last = Path.Combine(root, "last.flame");
            FlameXmlSerializer.Save(genome, first); FlameXmlSerializer.Save(genome, last);
            var missing = Path.Combine(root, "missing.flame");
            var settings = new RenderSettings { Width = 32, Height = 32, SampleBudget = 2_000, Gamma = 1 };
            var session = new FlameRenderSession(new CpuFlameRenderer());
            var batch = new FlameBatchRerenderer(session);
            var results = await batch.RenderAsync([first, missing, last], genome, settings, 0, ToneCurve.Identity, false, true,
                (_, _, _) => { }, null, null, CancellationToken.None);
            Assert.Equal(3, results.Count);
            Assert.NotNull(results[0].OutputPath); Assert.NotNull(results[1].Error); Assert.NotNull(results[2].OutputPath);

            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batch.RenderAsync([first, last], genome, settings, 0, ToneCurve.Identity, false, true,
                (_, _, _) => { }, null, (_, _, _, _) => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
            var files = Directory.GetFiles(Path.Combine(root, "rerendered"));
            Assert.Equal(3, files.Length);
            Assert.DoesNotContain(files, file => file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
            Assert.True(File.Exists(Path.Combine(root, "rerendered", "first_rerender_002.png")));
            Assert.False(File.Exists(Path.Combine(root, "rerendered", "last_rerender_002.png")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void HighPrecisionCurveRetainsSubByteDifferencesAfterSmoothing()
    {
        var values = Enumerable.Range(0, 1024).Select(i => 0.3f + i * 0.0003f).ToArray();
        var frame = new HighPrecisionFrame(1024, 1, 1, values);
        var smoothed = PrecisionFrameProcessor.Smooth(frame, 0.5, 1, CancellationToken.None);
        var curve = new ToneCurve([new(0.2, 0), new(0.8, 1)]);
        var result = PrecisionFrameProcessor.ApplyCurve(smoothed, curve, CancellationToken.None);
        Assert.True(result.Frame.Pixels.Distinct().Count() > 256);
        Assert.Contains(result.Frame.Pixels, value => value % 257 != 0);
        Assert.Equal(1024, result.Histogram.Red.Sum());
        Assert.Equal(result.Histogram.Red, result.Histogram.Green);
        Assert.Equal(result.Histogram.Red, result.Histogram.Blue);
    }

    [Fact]
    public void ExactInputEndpointsMapToExactBlackAndWhiteBetweenLookupEntries()
    {
        float black = 0.50001f, white = 0.70001f;
        var frame = new HighPrecisionFrame(2, 1, 1, [black, white]);
        var curve = new ToneCurve([new(black, 0), new(white, 1)]);
        var processed = PrecisionFrameProcessor.ApplyCurve(frame, curve, CancellationToken.None);
        Assert.Equal(new ushort[] { 0, 65535 }, processed.Frame.Pixels);
    }

    [Fact]
    public void HistogramBinsUseTheirActualIntervalsAndOutputPixels()
    {
        var frame = new HighPrecisionFrame(4, 1, 1, [0f, 0.5f, 0.7f, 1f]);
        var processed = PrecisionFrameProcessor.ApplyCurve(frame, ToneCurve.Identity, CancellationToken.None);
        Assert.Equal(1, processed.Histogram.Red[0]);
        Assert.Equal(1, processed.Histogram.Red[128]);
        Assert.Equal(1, processed.Histogram.Red[179]);
        Assert.Equal(1, processed.Histogram.Red[255]);
        Assert.Equal(0, processed.Histogram.Minimum);
        Assert.Equal(1, processed.Histogram.Maximum);
        var output = processed.Frame.ColorHistogram!;
        Assert.Equal(1, output.Red[0]);
        Assert.Equal(1, output.Red[128]);
        Assert.Equal(1, output.Red[179]);
        Assert.Equal(1, output.Red[255]);
        Assert.Equal(0, output.Minimum);
        Assert.Equal(1, output.Maximum);
    }

    [Fact]
    public void FittingInputExtremaMapsThemToFullRangeEvenWhenTheyAreVeryClose()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                float minimum = 0.500001f, maximum = 0.500005f;
                var source = new HighPrecisionFrame(3, 1, 1, [minimum, (minimum + maximum) / 2, maximum]);
                var input = PrecisionFrameProcessor.ApplyCurve(source, ToneCurve.Identity, CancellationToken.None);
                var editor = new ToneCurveEditor { Histogram = input.Histogram };
                Assert.True(editor.FitInputRange());
                Assert.Equal((double)minimum, editor.Curve.Points[0].X);
                Assert.Equal((double)maximum, editor.Curve.Points[^1].X);
                var fitted = PrecisionFrameProcessor.ApplyCurve(source, editor.Curve, CancellationToken.None).Frame;
                Assert.Equal((ushort)0, fitted.Pixels[0]);
                Assert.InRange((int)fitted.Pixels[1], 30_000, 36_000);
                Assert.Equal(ushort.MaxValue, fitted.Pixels[2]);
                Assert.Equal(1, fitted.ColorHistogram!.Red[0]);
                Assert.Equal(1, fitted.ColorHistogram.Red[255]);
                editor.Histogram = new RgbHistogram(new long[256], new long[256], new long[256]) { Minimum = 0.5, Maximum = 0.5 };
                Assert.False(editor.FitInputRange());
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void HistogramBarDoesNotExtendIntoAnEmptyAdjacentBin()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var bins = new long[256]; bins[100] = 1;
                var chart = new ToneCurveEditor { Width = 272, Height = 210, Histogram = new RgbHistogram(bins, bins, bins) };
                chart.Measure(new System.Windows.Size(272, 210));
                chart.Arrange(new System.Windows.Rect(0, 0, 272, 210));
                chart.UpdateLayout();
                var drawing = System.Windows.Media.VisualTreeHelper.GetDrawing(chart);
                var plot = Assert.Single(drawing.Children.OfType<System.Windows.Media.DrawingGroup>());
                var bar = Assert.Single(plot.Children.OfType<System.Windows.Media.GeometryDrawing>(),
                    item => item.Geometry is System.Windows.Media.RectangleGeometry);
                var bounds = ((System.Windows.Media.RectangleGeometry)bar.Geometry).Rect;
                Assert.Equal(108, bounds.Left);
                Assert.Equal(109, bounds.Right);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void HistogramDefaultsToLogarithmicScaleAndHidesBinsAtTheDisplayFloor()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var bins = new long[256]; bins[50] = 1; bins[75] = 100; bins[100] = 1000;
                var chart = new ToneCurveEditor { Width = 272, Height = 210, Histogram = new RgbHistogram(bins, bins, bins) };
                chart.Measure(new System.Windows.Size(272, 210));
                chart.Arrange(new System.Windows.Rect(0, 0, 272, 210));
                chart.UpdateLayout();
                Dictionary<double, double> BarHeights()
                {
                    var drawing = System.Windows.Media.VisualTreeHelper.GetDrawing(chart);
                    var plot = Assert.Single(drawing.Children.OfType<System.Windows.Media.DrawingGroup>());
                    return plot.Children.OfType<System.Windows.Media.GeometryDrawing>()
                        .Where(item => item.Geometry is System.Windows.Media.RectangleGeometry)
                        .Select(item => ((System.Windows.Media.RectangleGeometry)item.Geometry).Rect)
                        .ToDictionary(rectangle => rectangle.Left, rectangle => rectangle.Height);
                }
                Assert.True(chart.UseLogarithmicHistogram);
                var logarithmic = BarHeights();
                Assert.False(logarithmic.ContainsKey(58));
                Assert.Equal(182 * 2d / 3, logarithmic[83], 6);
                Assert.Equal(182, logarithmic[108], 6);
                chart.UseLogarithmicHistogram = false;
                chart.UpdateLayout();
                var linear = BarHeights();
                Assert.Equal(0.182, linear[58], 6);
                Assert.Equal(18.2, linear[83], 6);
                Assert.Equal(1000, bins[100]);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(0, 0.2, 0.25, 0.0)]
    [InlineData(1, 0.8, 0.85, 1.0)]
    public void InputLevelMarkerRestoresBlackOrWhiteOutput(int selected, double previousOutput, double input, double expectedOutput)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var editor = new ToneCurveEditor();
                var type = typeof(ToneCurveEditor);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                type.GetField("_selected", flags)!.SetValue(editor, selected);
                type.GetMethod("MoveSelected", flags)!.Invoke(editor, [selected == 0 ? 0d : 1d, previousOutput]);
                type.GetMethod("MoveInputMarkerFromScreen", flags)!.Invoke(editor,
                    [new System.Windows.Rect(0, 0, 100, 100), new System.Windows.Point(input * 100, 110)]);
                Assert.Equal(input, editor.Curve.Points[selected].X, 8);
                Assert.Equal(expectedOutput, editor.Curve.Points[selected].Y);
                var pixel = new HighPrecisionFrame(1, 1, 1, [(float)input]);
                var adjusted = PrecisionFrameProcessor.ApplyCurve(pixel, editor.Curve, CancellationToken.None);
                Assert.Equal(selected == 0 ? (ushort)0 : ushort.MaxValue, adjusted.Frame.Pixels[0]);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneRenderRetainsPrecisionAndMatchesExistingTone(bool color)
    {
        var genome = new FlameGenerator().Generate(777);
        if (color) genome.Palette = PaletteDefinition.Fire;
        var settings = new RenderSettings { Width = 64, Height = 64, SampleBudget = 300_000, Oversample = 2, Gamma = 1 };
        var renderer = new CpuFlameRenderer();
        var oldFrame = await renderer.RenderAsync(genome, settings, null, CancellationToken.None);
        var session = new FlameRenderSession(renderer);
        var highFrame = (await session.PrepareAsync(genome, settings, 0, null, CancellationToken.None)).Frame;
        Assert.Equal(color ? 3 : 1, highFrame.Channels);
        Assert.True(highFrame.Pixels.Distinct().Count() > 256);
        for (var i = 0; i < 64 * 64; i++)
            for (var channel = 0; channel < highFrame.Channels; channel++)
            {
                var oldIndex = i * 4 + (color ? 2 - channel : 0);
                var newValue = highFrame.Pixels[i * highFrame.Channels + channel] / 257d;
                Assert.InRange(Math.Abs(newValue - oldFrame.BgraPixels[oldIndex]), 0, 2.1);
            }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(3, 2)]
    public void StandalonePngStoresSixteenBitsPerChannel(int channels, byte colorType)
    {
        var root = NewTempDirectory();
        try
        {
            var values = Enumerable.Range(0, 128 * channels).Select(i => (i + 1) / (float)(128 * channels + 1)).ToArray();
            var input = new HighPrecisionFrame(128, 1, channels, values);
            var result = PrecisionFrameProcessor.ApplyCurve(input, new ToneCurve([new(0.1, 0), new(0.9, 1)]), CancellationToken.None);
            var path = Path.Combine(root, "precision.png");
            result.Frame.SavePng(path);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal((byte)16, bytes[24]);
            Assert.Equal(colorType, bytes[25]);
            var decoded = new System.Windows.Media.Imaging.PngBitmapDecoder(new Uri(path), System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var expectedFormat = channels == 1 ? System.Windows.Media.PixelFormats.Gray16 : System.Windows.Media.PixelFormats.Rgb48;
            var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(decoded, expectedFormat, null, 0);
            var decodedPixels = new ushort[result.Frame.Pixels.Length];
            converted.CopyPixels(decodedPixels, 128 * channels * 2, 0);
            Assert.Equal(result.Frame.Pixels, decodedPixels);
            var outputHistogram = result.Frame.ColorHistogram!;
            var expectedChannels = new[] { outputHistogram.Red, outputHistogram.Green, outputHistogram.Blue };
            for (var channel = 0; channel < 3; channel++)
            {
                var expected = new long[256];
                for (var pixel = 0; pixel < 128; pixel++) expected[decodedPixels[pixel * channels + (channels == 1 ? 0 : channel)] >> 8]++;
                Assert.Equal(expected, expectedChannels[channel]);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ParallelRendererReportsTheFullBudgetAndCancels()
    {
        var genome = new FlameGenerator().Generate(123);
        var renderer = new CpuFlameRenderer();
        var progress = new RecordingProgress();
        var settings = new RenderSettings { Width = 64, Height = 64, SampleBudget = 30_000, WorkerCount = 4 };
        await renderer.RenderAsync(genome, settings, progress, CancellationToken.None);
        Assert.Equal(settings.SampleBudget, progress.Last.CompletedSamples);
        using var cancellation = new CancellationTokenSource();
        var cancelDuringSampling = new CancellingProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(genome, settings with { SampleBudget = 20_000_000 }, cancelDuringSampling, cancellation.Token));
        Assert.True(cancelDuringSampling.SawSamples);
    }

    [Theory]
    [InlineData(1, 20_000_000_000L)]
    [InlineData(4, 20_000_000_000L)]
    [InlineData(1, 1_000_000_000_000L)]
    [InlineData(4, 1_000_000_000_000L)]
    public async Task LargeSampleRenderReportsTheFullBudgetAndCancels(int workers, long budget)
    {
        var genome = new FlameGenerator().Generate(123);
        var settings = new RenderSettings { Width = 64, Height = 64, SampleBudget = budget, WorkerCount = workers };
        using var cancellation = new CancellationTokenSource();
        var progress = new CancellingProgress(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CpuFlameRenderer().RenderAsync(genome, settings, progress, cancellation.Token));
        Assert.True(progress.SawSamples);
        Assert.Equal(budget, progress.TotalSamples);
    }

    [Fact]
    public async Task RendererProducesAnImageLargerThanThePreviousLimit()
    {
        var genome = new FlameGenerator().Generate(123);
        var frame = await new CpuFlameRenderer().RenderAsync(genome,
            new RenderSettings { Width = 4097, Height = 4097, SampleBudget = 100 }, null, CancellationToken.None);
        Assert.Equal(4097, frame.Width);
        Assert.Equal(4097, frame.Height);
        Assert.Equal(4097 * 4097 * 4, frame.BgraPixels.Length);
    }

    [Fact]
    public void SmoothingReducesAnIsolatedPixelWithoutChangingTheInput()
    {
        var pixels = Enumerable.Repeat((byte)255, 7 * 7 * 4).ToArray();
        var center = (3 * 7 + 3) * 4;
        pixels[center] = pixels[center + 1] = pixels[center + 2] = 0;
        var frame = new RenderedFrame(7, 7, pixels);
        var smoothed = FrameSmoother.Apply(frame, 1, 2, CancellationToken.None);
        Assert.Equal(0, frame.BgraPixels[center]);
        Assert.InRange(smoothed.BgraPixels[center], (byte)1, (byte)254);
        Assert.True(smoothed.BgraPixels[center + 4] < 255);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FrameSmoother.Apply(frame, 1, 2, cancellation.Token));
    }

    [Fact]
    public void OptionalDensityNormalizationKeepsToneStableWhenCountsAndBudgetScaleTogether()
    {
        var counts = new[] { 0d, 4d, 16d, 100d };
        var settings = new RenderSettings { SampleBudget = 20_000_000, Gamma = 1, NormalizeDensityBySampleCount = true };
        var baseline = ToneMapper.Map(counts, counts, counts, counts, settings, true);
        var denser = counts.Select(count => count * 10).ToArray();
        var scaled = ToneMapper.Map(denser, denser, denser, denser, settings with { SampleBudget = 200_000_000 }, true);
        Assert.Equal(baseline, scaled);
    }

    [Fact]
    public void MasterCurveIsSmoothAndDoesNotOvershootItsControlPoints()
    {
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (byte)i), ToneCurve.Identity.CreateLookup());
        var curve = new ToneCurve([new(0, 0), new(0.4, 0.8), new(1, 1)]);
        var lookup = curve.CreateLookup();
        Assert.InRange(lookup[102], (byte)203, (byte)205);
        Assert.True(lookup.Zip(lookup.Skip(1), (left, right) => left <= right).All(value => value));
        Assert.Equal(0, lookup[0]);
        Assert.Equal(255, lookup[255]);
        var inverted = new ToneCurve([new(0, 1), new(1, 0)]).CreateLookup();
        Assert.Equal(255, inverted[0]);
        Assert.Equal(0, inverted[255]);
        var peak = new ToneCurve([new(0, 0), new(0.5, 1), new(1, 0)]).CreateLookup();
        Assert.True(peak[100] < peak[128]);
        Assert.True(peak[200] < peak[128]);
        var levels = new ToneCurve([new(0.2, 0), new(0.8, 1)]).CreateLookup();
        Assert.All(levels.Take(52), value => Assert.Equal(0, value));
        Assert.All(levels.Skip(204), value => Assert.Equal(255, value));
        Assert.InRange(levels[128], (byte)127, (byte)129);
        Assert.Throws<ArgumentException>(() => new ToneCurve([new(0, 0), new(0.5, 0.5), new(0.5, 0.7), new(1, 1)]));
    }

    [Fact]
    public void MasterCurveUpdatesEveryRgbChannelAndReportsTheInputHistogram()
    {
        var frame = new RenderedFrame(2, 1, [10, 20, 30, 255, 200, 150, 100, 128]);
        var histogram = BitmapToneCurve.Apply(frame, new ToneCurve([new(0, 1), new(1, 0)]), CancellationToken.None);
        Assert.Equal(new byte[] { 245, 235, 225, 255, 55, 105, 155, 128 }, frame.BgraPixels);
        Assert.Equal(1, histogram.Red[30]);
        Assert.Equal(1, histogram.Green[150]);
        Assert.Equal(1, histogram.Blue[200]);
        Assert.Equal(2, histogram.Red.Sum());
        Assert.Equal(2, histogram.Green.Sum());
        Assert.Equal(2, histogram.Blue.Sum());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => BitmapToneCurve.Apply(frame, ToneCurve.Identity, cancellation.Token));
    }

    [Fact]
    public void ViewportCanFitAHighResolutionImageInASmallArea()
    {
        Assert.Equal(0.03, ViewportZoom.Fit(300, 400, 10_000, 10_000), 8);
        Assert.Equal(ViewportZoom.Minimum, ViewportZoom.Clamp(0));
        Assert.Equal(ViewportZoom.Maximum, ViewportZoom.Clamp(100));
    }

    [Fact]
    public async Task DefaultRenderIsSquare2048AndMonochrome()
    {
        var genome = new FlameGenerator().Generate(314159);
        var settings = new RenderSettings { SampleBudget = 1_000 };
        Assert.Equal(20_000_000, new RenderSettings().SampleBudget);
        Assert.Equal(0, new RenderSettings().LowDensityCutoff);
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
    public void GeneratedVariationWeightsRespectTheConfiguredFloorAndAreNormalized()
    {
        var settings = StableGeneratorSettings() with
        {
            MinimumVariationCount = 3,
            MaximumVariationCount = 3,
            EnabledVariations = ["linear", "sinusoidal", "spherical"],
            MinimumVariationShare = 0.05
        };

        var genome = new FlameGenerator().Generate(781, new FlameGeneratorOptions { GeneratorSettings = settings });

        Assert.All(genome.Transforms, transform =>
        {
            Assert.Equal(1, transform.Variations.Values.Sum(), 10);
            Assert.All(transform.Variations.Values, weight => Assert.InRange(weight, 0.05, 0.90));
        });
    }

    [Fact]
    public void SpaceFillingSimplexSamplerIsDeterministicAndValidForEverySupportedCount()
    {
        var first = new SpaceFillingSimplexSampler(2468);
        var second = new SpaceFillingSimplexSampler(2468);
        var firstRandom = new DeterministicRandom(1357);
        var secondRandom = new DeterministicRandom(1357);

        for (var sample = 0; sample < 1_000; sample++)
        {
            for (var count = 1; count <= 5; count++)
            {
                var firstWeights = first.NextWeights(count, 0.05, firstRandom);
                var secondWeights = second.NextWeights(count, 0.05, secondRandom);
                Assert.Equal(firstWeights, secondWeights);
                Assert.Equal(1, firstWeights.Sum(), 10);
                Assert.All(firstWeights, weight => Assert.InRange(weight, 0.05, 1 - (count - 1) * 0.05));
            }
        }
    }

    [Fact]
    public void TwoVariationSimplexSamplesCoverTheConfiguredInterval()
    {
        const double minimumShare = 0.05;
        const int binCount = 18;
        var sampler = new SpaceFillingSimplexSampler(9876);
        var random = new DeterministicRandom(5432);
        var samples = Enumerable.Range(0, 4_096)
            .Select(_ => sampler.NextWeights(2, minimumShare, random)[0])
            .ToArray();
        var bins = new int[binCount];
        foreach (var sample in samples)
        {
            var bin = Math.Min(binCount - 1, (int)((sample - minimumShare) / (1 - 2 * minimumShare) * binCount));
            bins[bin]++;
        }

        Assert.True(samples.Min() < 0.051);
        Assert.True(samples.Max() > 0.949);
        Assert.All(bins, count => Assert.True(count > 150, $"A ratio interval received only {count} samples."));
    }

    [Fact]
    public void ThreeVariationSimplexSamplesReachEveryDominantCorner()
    {
        var sampler = new SpaceFillingSimplexSampler(1122);
        var random = new DeterministicRandom(3344);
        var samples = Enumerable.Range(0, 8_192)
            .Select(_ => sampler.NextWeights(3, 0.05, random))
            .ToArray();

        for (var component = 0; component < 3; component++)
            Assert.Contains(samples, weights => weights[component] > 0.85);
        Assert.Contains(samples, weights => weights.All(weight => weight is > 0.28 and < 0.39));
    }

    [Fact]
    public void MinimumVariationShareCannotExceedTheSimplexCapacity()
    {
        var settings = StableGeneratorSettings() with
        {
            MinimumVariationCount = 3,
            MaximumVariationCount = 3,
            EnabledVariations = ["linear", "sinusoidal", "spherical"],
            MinimumVariationShare = 0.34
        };

        Assert.Throws<InvalidDataException>(settings.ThrowIfInvalid);
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
            while ((service.Status.Completed < 2 || service.Status.IsRunning) && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(2, service.Status.Completed);
            Assert.False(service.Status.IsRunning);
            Assert.Equal(2, service.Status.ReadyCount);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SessionGenomesDoNotDependOnRenderWorkerTiming()
    {
        var firstRoot = NewTempDirectory();
        var secondRoot = NewTempDirectory();
        try
        {
            var generatorSettings = StableGeneratorSettings() with
            {
                MinimumVariationCount = 3,
                MaximumVariationCount = 3,
                EnabledVariations = ["linear", "sinusoidal", "spherical"]
            };
            var common = new ContinuousRenderOptions
            {
                BatchLimit = 20,
                QueueCapacity = 2,
                Seed = 24680,
                SessionId = "determinism",
                GeneratorSettings = generatorSettings,
                RenderSettings = new RenderSettings { Width = 32, Height = 32, SampleBudget = 100 }
            };

            await using (var first = new ContinuousRenderService(new FlameGenerator(), new FastRenderer()))
            {
                var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
                first.RenderFailed += failures.Enqueue;
                first.Start(common with { OutputDirectory = firstRoot, WorkerCount = 1 });
                await WaitForSession(first, common.BatchLimit);
                Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Select(error => error.ToString())));
            }
            await using (var second = new ContinuousRenderService(new FlameGenerator(), new FastRenderer()))
            {
                var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
                second.RenderFailed += failures.Enqueue;
                second.Start(common with { OutputDirectory = secondRoot, WorkerCount = Math.Min(2, Environment.ProcessorCount) });
                await WaitForSession(second, common.BatchLimit);
                Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Select(error => error.ToString())));
            }

            var firstFlames = Directory.EnumerateFiles(Path.Combine(firstRoot, "rendered"), "*.flame")
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText)
                .ToArray();
            var secondFlames = Directory.EnumerateFiles(Path.Combine(secondRoot, "rendered"), "*.flame")
                .OrderBy(Path.GetFileName)
                .Select(File.ReadAllText)
                .ToArray();
            Assert.Equal(firstFlames, secondFlames);
            var profile = File.ReadAllText(Path.Combine(firstRoot, "rendered", "generator_profile_run_determinism.json"));
            Assert.Contains($"\"variation_weight_sampler_version\": \"{SpaceFillingSimplexSampler.Version}\"", profile);
            Assert.Contains("\"minimum_variation_share\": 0.05", profile);
        }
        finally
        {
            Directory.Delete(firstRoot, true);
            Directory.Delete(secondRoot, true);
        }

        static async Task WaitForSession(ContinuousRenderService service, int expectedCount)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (service.Status.Completed + service.Status.Failed < expectedCount && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(expectedCount, service.Status.Completed + service.Status.Failed);
        }
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
    public void CopyRatingKeepsRenderedPairHidesItFromCatalogAndUndoRestoresThePreviousState()
    {
        var root = NewTempDirectory();
        try
        {
            var archive = new SourceArchive(root);
            var artifact = archive.Save(new FlameGenerator().Generate(199), new RenderedFrame(8, 8, new byte[8 * 8 * 4]), 1);
            var ratings = new RatingStore(root);
            ratings.Rate(artifact.ImagePath, 2, copyFiles: true);
            var ratingTwoImage = Path.Combine(root, "ratings", "2", artifact.SourceId + ".png");
            var ratingFiveImage = Path.Combine(root, "ratings", "5", artifact.SourceId + ".png");

            Assert.True(File.Exists(artifact.ImagePath));
            Assert.True(File.Exists(artifact.FlamePath));
            Assert.True(File.Exists(ratingTwoImage));
            var catalog = new CandidateCatalog();
            catalog.Refresh(archive, ratings);
            Assert.Empty(catalog.Ordered(false));

            ratings.Rate(ratingTwoImage, 5, copyFiles: true);
            Assert.True(File.Exists(artifact.ImagePath));
            Assert.False(File.Exists(ratingTwoImage));
            Assert.True(File.Exists(ratingFiveImage));
            Assert.True(ratings.Undo());
            Assert.True(File.Exists(ratingTwoImage));
            Assert.False(File.Exists(ratingFiveImage));
            Assert.True(ratings.Undo());
            Assert.False(File.Exists(ratingTwoImage));
            Assert.True(File.Exists(artifact.ImagePath));
            catalog.Refresh(archive, ratings);
            Assert.Single(catalog.Ordered(false));
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

    [Fact]
    public void CameraPreviewUsesTheSavedCameraAsItsBaseline()
    {
        var savedCamera = new FlameCamera(0.5, -0.25, 150, 30);

        var unchanged = savedCamera.RelativePreviewFrom(savedCamera, 2048, 2048);
        var rotated = (savedCamera with { Rotate = 120 }).RelativePreviewFrom(savedCamera, 2048, 2048);

        Assert.Equal(CameraPreviewTransform.Identity, unchanged);
        Assert.Equal(0, rotated.M11, 10);
        Assert.Equal(1, rotated.M12, 10);
        Assert.Equal(-1, rotated.M21, 10);
        Assert.Equal(0, rotated.M22, 10);
    }

    [Fact]
    public async Task RerenderWithCameraPersistsTheCameraAfterSuccess()
    {
        var root = NewTempDirectory();
        try
        {
            var genome = new FlameGenerator().Generate(902);
            var archive = new SourceArchive(root);
            var artifact = archive.Save(genome, new RenderedFrame(4, 4, Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray()), 1);
            var camera = new FlameCamera(0.5, -0.25, 150, 30);
            var renderer = new CameraCapturingRenderer();

            await new ArtifactRerenderer(renderer).RerenderAsync(
                artifact,
                PaletteDefinition.Fire,
                new RenderSettings { Width = 4, Height = 4, SampleBudget = 100 },
                null,
                CancellationToken.None,
                camera);

            Assert.Equal(camera, FlameCamera.From(FlameXmlSerializer.Load(artifact.FlamePath)));
            Assert.Equal(camera, renderer.RenderedCamera);
            Assert.True(SourceArchive.IsCompleteCandidate(artifact.ImagePath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancelledCameraRerenderPreservesTheExistingPair()
    {
        var root = NewTempDirectory();
        try
        {
            var genome = new FlameGenerator().Generate(903);
            var archive = new SourceArchive(root);
            var artifact = archive.Save(genome, new RenderedFrame(4, 4, Enumerable.Repeat((byte)255, 4 * 4 * 4).ToArray()), 1);
            var originalFlame = File.ReadAllText(artifact.FlamePath);
            var originalImage = File.ReadAllBytes(artifact.ImagePath);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArtifactRerenderer(new ReplacementRenderer()).RerenderAsync(
                artifact,
                PaletteDefinition.Fire,
                new RenderSettings { Width = 4, Height = 4, SampleBudget = 100 },
                null,
                cancellation.Token,
                new FlameCamera(0.5, -0.25, 150, 30)));

            Assert.Equal(originalFlame, File.ReadAllText(artifact.FlamePath));
            Assert.Equal(originalImage, File.ReadAllBytes(artifact.ImagePath));
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
        MinimumVariationShare = 0.05,
        PostTransformChance = 0,
        AllowFinalTransforms = false
    };

    private sealed class ThrowingRenderer : IFlameRenderer
    {
        public RendererStatus Status { get; } = new("CPU", "Test CPU", false, "Test renderer");
        public Task<RenderedFrame> RenderAsync(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken) => throw new InvalidOperationException("synthetic renderer failure");
    }

    private sealed class CountingHistogramRenderer : IHistogramFlameRenderer
    {
        private readonly CpuFlameRenderer _inner = new();
        public int Samples { get; private set; }
        public int Maps { get; private set; }
        public bool FailNextMap { get; set; }
        public RendererStatus Status => _inner.Status;
        public Task<RenderedFrame> RenderAsync(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken) =>
            _inner.RenderAsync(genome, settings, progress, cancellationToken);
        public Task<FlameHistogram> SampleAsync(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
        {
            Samples++;
            return _inner.SampleAsync(genome, settings, progress, cancellationToken);
        }
        public Task<RenderedFrame> MapAsync(FlameHistogram histogram, RenderSettings settings, CancellationToken cancellationToken)
        {
            Maps++;
            if (FailNextMap)
            {
                FailNextMap = false;
                throw new InvalidOperationException("synthetic mapping failure");
            }
            return _inner.MapAsync(histogram, settings, cancellationToken);
        }
        public Task<HighPrecisionFrame> MapHighPrecisionAsync(FlameHistogram histogram, RenderSettings settings, CancellationToken cancellationToken)
        {
            Maps++;
            if (FailNextMap)
            {
                FailNextMap = false;
                throw new InvalidOperationException("synthetic mapping failure");
            }
            return _inner.MapHighPrecisionAsync(histogram, settings, cancellationToken);
        }
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

    private sealed class CameraCapturingRenderer : IFlameRenderer
    {
        public RendererStatus Status { get; } = new("CPU", "Test CPU", false, "Test renderer");
        public FlameCamera? RenderedCamera { get; private set; }

        public Task<RenderedFrame> RenderAsync(FlameGenome genome, RenderSettings settings, IProgress<RenderProgress>? progress, CancellationToken cancellationToken)
        {
            RenderedCamera = FlameCamera.From(genome);
            return Task.FromResult(new RenderedFrame(4, 4, Enumerable.Repeat((byte)0, 4 * 4 * 4).ToArray()));
        }
    }

    private sealed class RecordingProgress : IProgress<RenderProgress>
    {
        public RenderProgress Last { get; private set; } = new(0, 0);
        public void Report(RenderProgress value) => Last = value;
    }

    private sealed class CancellingProgress(CancellationTokenSource cancellation) : IProgress<RenderProgress>
    {
        public bool SawSamples { get; private set; }
        public long TotalSamples { get; private set; }
        public void Report(RenderProgress value)
        {
            TotalSamples = value.TotalSamples;
            if (value.CompletedSamples <= 0) return;
            SawSamples = true;
            cancellation.Cancel();
        }
    }
}
