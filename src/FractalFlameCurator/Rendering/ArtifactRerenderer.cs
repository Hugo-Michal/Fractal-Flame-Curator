using FractalFlameCurator.Models;
using FractalFlameCurator.Serialization;
using FractalFlameCurator.Storage;

namespace FractalFlameCurator.Rendering;

public sealed class ArtifactRerenderer
{
    private readonly IFlameRenderer _renderer;

    public ArtifactRerenderer(IFlameRenderer renderer)
    {
        _renderer = renderer;
    }

    public async Task RerenderAsync(
        RenderedArtifact artifact,
        PaletteDefinition palette,
        RenderSettings settings,
        IProgress<RenderProgress>? progress,
        CancellationToken cancellationToken,
        FlameCamera? camera = null)
    {
        if (!SourceArchive.IsCompleteCandidate(artifact.ImagePath))
        {
            throw new InvalidDataException("The current image and matching source .flame file are not both available.");
        }

        var sourceGenome = FlameXmlSerializer.Load(artifact.FlamePath);
        var renderGenome = sourceGenome.Clone();
        renderGenome.Palette = palette;
        renderGenome.Width = settings.Width;
        renderGenome.Height = settings.Height;
        renderGenome.Quality = settings.SampleBudget;
        renderGenome.Oversample = settings.Oversample;
        renderGenome.FilterRadius = settings.FilterRadius;
        renderGenome.Gamma = settings.Gamma;
        renderGenome.Brightness = settings.Brightness;
        renderGenome.Vibrancy = settings.Vibrancy;
        camera?.ApplyTo(renderGenome);

        var temporaryImagePath = artifact.ImagePath + ".rerender-" + Guid.NewGuid().ToString("N") + ".tmp";
        var temporaryFlamePath = artifact.FlamePath + ".rerender-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var frame = await _renderer.RenderAsync(renderGenome, settings, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            frame.SavePng(temporaryImagePath);
            cancellationToken.ThrowIfCancellationRequested();
            if (camera is null)
            {
                File.Move(temporaryImagePath, artifact.ImagePath, true);
                return;
            }

            camera.Value.ApplyTo(sourceGenome);
            FlameXmlSerializer.Save(sourceGenome, temporaryFlamePath);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceCandidatePair(artifact.ImagePath, temporaryImagePath, artifact.FlamePath, temporaryFlamePath);
        }
        finally
        {
            if (File.Exists(temporaryImagePath)) File.Delete(temporaryImagePath);
            if (File.Exists(temporaryFlamePath)) File.Delete(temporaryFlamePath);
        }
    }

    private static void ReplaceCandidatePair(string imagePath, string temporaryImagePath, string flamePath, string temporaryFlamePath)
    {
        var suffix = ".rerender-backup-" + Guid.NewGuid().ToString("N");
        var imageBackupPath = imagePath + suffix;
        var flameBackupPath = flamePath + suffix;
        var completed = false;
        try
        {
            File.Move(flamePath, flameBackupPath);
            File.Move(imagePath, imageBackupPath);
            File.Move(temporaryFlamePath, flamePath);
            File.Move(temporaryImagePath, imagePath);
            completed = true;
        }
        catch
        {
            if (File.Exists(flameBackupPath)) File.Move(flameBackupPath, flamePath, true);
            if (File.Exists(imageBackupPath)) File.Move(imageBackupPath, imagePath, true);
            throw;
        }
        finally
        {
            if (completed && File.Exists(flameBackupPath)) File.Delete(flameBackupPath);
            if (completed && File.Exists(imageBackupPath)) File.Delete(imageBackupPath);
        }
    }
}
