using FractalFlameCurator.Models;
using FractalFlameCurator.Serialization;

namespace FractalFlameCurator.Rendering;

public sealed record FlameBatchProgress(int Index, int Total, string SourcePath, RenderProgress? Sampling);
public sealed record FlameBatchResult(string SourcePath, string? OutputPath, string? Error);

public sealed class FlameBatchRerenderer(FlameRenderSession session)
{
    public async Task<IReadOnlyList<FlameBatchResult>> RenderAsync(
        IReadOnlyList<string> paths, FlameGenome template, RenderSettings settings, double smoothing,
        ToneCurve curve, bool automaticCurve, bool useSourcePalette,
        Action<RenderSettings, PaletteDefinition, double> checkMemory,
        IProgress<FlameBatchProgress>? progress, Func<string, FlameGenome, PreparedFlameFrame, string, Task>? completed,
        CancellationToken cancellationToken)
    {
        var results = new List<FlameBatchResult>();
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = paths[index];
            var itemIndex = index + 1;
            string? outputPath = null;
            progress?.Report(new FlameBatchProgress(itemIndex, paths.Count, path, null));
            session.Clear();
            try
            {
                var genome = await Task.Run(() => FlameXmlSerializer.Load(path), cancellationToken);
                FlameCamera.From(template).ApplyTo(genome);
                if (!useSourcePalette) genome.Palette = template.Palette;
                if (genome.Palette.Colors.SequenceEqual(PaletteDefinition.Monochrome.Colors)) genome.Palette = PaletteDefinition.Monochrome;
                genome.Width = settings.Width; genome.Height = settings.Height; genome.Quality = settings.SampleBudget;
                genome.Oversample = settings.Oversample; genome.FilterRadius = settings.FilterRadius;
                genome.Gamma = settings.Gamma; genome.Brightness = settings.Brightness; genome.Vibrancy = settings.Vibrancy;
                var itemSettings = settings with { PaletteName = genome.Palette.Name };
                checkMemory(itemSettings, genome.Palette, smoothing);
                var itemProgress = new SamplingProgress(value => progress?.Report(new FlameBatchProgress(itemIndex, paths.Count, path, value)));
                var prepared = await session.PrepareAsync(genome, itemSettings, smoothing, itemProgress, cancellationToken, curve, automaticCurve);
                outputPath = await SaveUniqueAsync(prepared.Frame, path, cancellationToken);
                session.Commit(prepared);
                if (completed is not null) await completed(path, genome, prepared, outputPath);
                results.Add(new FlameBatchResult(path, outputPath, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                session.Clear();
                results.Add(new FlameBatchResult(path, outputPath, exception.Message));
            }
        }
        return results;
    }

    private static async Task<string> SaveUniqueAsync(RenderedFrame16 frame, string sourcePath, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath))!, "rerendered");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".rerender-{Guid.NewGuid():N}.tmp");
        try
        {
            await Task.Run(() => frame.SavePng(temporary), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(sourcePath) + "_rerender";
            for (var index = 1; ; index++)
            {
                var suffix = index == 1 ? string.Empty : $"_{index:D3}";
                var destination = Path.Combine(directory, name + suffix + ".png");
                try { File.Move(temporary, destination); return destination; }
                catch (IOException) when (File.Exists(destination)) { }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class SamplingProgress(Action<RenderProgress> report) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value) => report(value);
    }
}
