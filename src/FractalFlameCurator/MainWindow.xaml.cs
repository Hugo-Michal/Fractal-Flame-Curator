using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FractalFlameCurator.Ai;
using FractalFlameCurator.Generation;
using FractalFlameCurator.Models;
using FractalFlameCurator.Pipeline;
using FractalFlameCurator.Rendering;
using FractalFlameCurator.Storage;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMessageBox = System.Windows.MessageBox;
using WpfPoint = System.Windows.Point;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfBrushes = System.Windows.Media.Brushes;

namespace FractalFlameCurator;

public partial class MainWindow : Window
{
    private readonly CpuFlameRenderer _renderer = new();
    private readonly ArtifactRerenderer _artifactRerenderer;
    private readonly ContinuousRenderService _renderService;
    private readonly DinoV2PreferenceBackend _aiBackend;
    private readonly ContinuousAiScoringService _aiService;
    private readonly CandidateCatalog _catalog = new();
    private readonly Dictionary<string, HashSet<string>> _seenSourceIdsByWorkspace = new(StringComparer.OrdinalIgnoreCase);
    private RandomGeneratorSettings _generatorSettings = RandomGeneratorSettings.CreateDefault();
    private SourceArchive? _archive;
    private RatingStore? _ratingStore;
    private RenderedArtifact? _current;
    private RenderedArtifact? _lastRated;
    private RenderedArtifact? _deferredAfterUndo;
    private bool _aiEnabled;
    private double _zoomScale = 1;
    private int _imagePixelWidth;
    private int _imagePixelHeight;
    private bool _fitToViewport = true;
    private bool _applyingZoom;
    private bool _closing;
    private bool _renderControlsLocked;
    private CancellationTokenSource? _rerenderCancellation;
    private Task? _activeRerender;
    private CancellationTokenSource? _ratedRerenderCancellation;
    private Task? _activeRatedRerender;
    private CancellationTokenSource? _ratedRescoreCancellation;
    private Task? _activeRatedRescore;
    private readonly DispatcherTimer _statusTimer;

    public MainWindow()
    {
        InitializeComponent();
        _artifactRerenderer = new ArtifactRerenderer(_renderer);
        _renderService = new ContinuousRenderService(new FlameGenerator(), _renderer);
        _renderService.ImageReady += RenderService_ImageReady;
        _renderService.RenderFailed += RenderService_RenderFailed;
        _aiBackend = new DinoV2PreferenceBackend(new DinoV2BackendOptions { ScriptPath = Path.Combine(AppContext.BaseDirectory, "Ai", "dinov2_service.py") });
        _aiService = new ContinuousAiScoringService(_aiBackend);
        _aiService.ImageScored += AiService_ImageScored;
        _aiService.ScoringFailed += AiService_ScoringFailed;
        _aiService.TrainingCompleted += AiService_TrainingCompleted;
        OutputDirectoryTextBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ApophysisCurator");
        SeedTextBox.Text = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        BatchLimitTextBox.Text = "100";
        WorkersTextBox.Text = Math.Max(1, Math.Min(4, Environment.ProcessorCount)).ToString(CultureInfo.InvariantCulture);
        QueueCapacityTextBox.Text = "4";
        SampleBudgetTextBox.Text = "20000000";
        OversampleComboBox.SelectedIndex = 0;
        FilterRadiusTextBox.Text = "0.5";
        GammaTextBox.Text = "1";
        BrightnessTextBox.Text = "1.0";
        WhitePointTextBox.Text = "0.0";
        BlackPointTextBox.Text = "0.85";
        ContrastCurveTextBox.Text = "1.0";
        LowDensityCutoffTextBox.Text = "0.01";
        VibrancyTextBox.Text = "1.0";
        PaletteComboBox.ItemsSource = PaletteDefinition.BuiltIns;
        PaletteComboBox.SelectedIndex = 0;
        BackendTextBlock.Text = $"Renderer: {_renderer.Status.Backend} · {_renderer.Status.Device}\n{_renderer.Status.Detail}";
        AiStatusTextBlock.Text = "AI diagnostics are loading…";
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _statusTimer.Tick += UpdateStatus;
        _statusTimer.Start();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var diagnostics = await _aiService.InitializeAsync();
            UpdateAiDiagnostics(diagnostics);
        }
        catch (Exception exception)
        {
            UpdateAiDiagnostics(DeviceDiagnostics.Unavailable(exception.Message));
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_renderControlsLocked) return;
        if (_renderService.Status.IsRunning)
        {
            await _renderService.StopAsync();
            UpdateRenderActionButtons();
            return;
        }

        try
        {
            EnsureWorkspace();
            var renderSettings = ReadImageRenderSettings(out var palette);
            var sessionOptions = new ContinuousRenderOptions
            {
                OutputDirectory = OutputDirectoryTextBox.Text.Trim(),
                BatchLimit = ParseInt(BatchLimitTextBox, 100, 1, 1_000_000),
                WorkerCount = ParseInt(WorkersTextBox, 1, 1, Math.Max(1, Environment.ProcessorCount)),
                QueueCapacity = ParseInt(QueueCapacityTextBox, 4, 1, 64),
                Seed = ParseLong(SeedTextBox, DateTime.UtcNow.Ticks),
                Palette = palette,
                RenderSettings = renderSettings,
                GeneratorSettings = _generatorSettings.Snapshot()
            };
            _renderService.Start(sessionOptions);
            UpdateRenderActionButtons();
            if (_current is null) ShowNextReady();
            else EmptyPreviewTextBlock.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            WpfMessageBox.Show(this, exception.Message, "Could not start rendering", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RandomGeneratorSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_renderService.Status.IsRunning) return;
        var dialog = new RandomGeneratorSettingsWindow(_generatorSettings) { Owner = this };
        if (dialog.ShowDialog() == true) _generatorSettings = dialog.AppliedSettings.Snapshot();
    }

    private async void RerenderCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRerender is not null)
        {
            _rerenderCancellation?.Cancel();
            return;
        }

        if (_activeRatedRerender is not null)
        {
            ImageSettingsStatusTextBlock.Text = "A rated-flame re-render is already running.";
            return;
        }

        _activeRerender = RerenderCurrentAsync();
        try { await _activeRerender; }
        catch (Exception exception) { ImageSettingsStatusTextBlock.Text = $"Re-render failed: {exception.Message}"; }
        finally { _activeRerender = null; }
    }

    private async Task RerenderCurrentAsync()
    {
        if (_current is null)
        {
            ImageSettingsStatusTextBlock.Text = "Select an unrated source image before re-rendering.";
            return;
        }

        if (_aiService.Status.IsRunning)
        {
            ImageSettingsStatusTextBlock.Text = "Stop AI scoring before re-rendering so its score cannot race the image replacement.";
            return;
        }

        EnsureWorkspace();
        if (_ratingStore?.FindRating(_current.ImagePath) is not null)
        {
            ImageSettingsStatusTextBlock.Text = "Rated images are protected. Undo the rating before re-rendering.";
            return;
        }

        var artifact = ResolveCurrentArtifact();
        var settings = ReadImageRenderSettings(out var palette);
        using var cancellation = new CancellationTokenSource();
        _rerenderCancellation = cancellation;
        RerenderCurrentButton.Content = "Cancel re-render";
        ImageSettingsStatusTextBlock.Text = "Re-rendering current flame…";
        try
        {
            var progress = new ThrottledRenderProgress(Dispatcher, update =>
            {
                ImageSettingsStatusTextBlock.Text = $"Re-rendering current flame… {update.CompletedSamples:N0}/{update.TotalSamples:N0} points";
            });
            await _artifactRerenderer.RerenderAsync(artifact, palette, settings, progress, cancellation.Token);
            await _aiService.InvalidateAsync(artifact.SourceId);
            _catalog.ClearScore(artifact.SourceId);
            RefreshCandidates();
            ShowArtifact(artifact);
            ImageSettingsStatusTextBlock.Text = "Current viewport updated. The source .flame file was preserved.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ImageSettingsStatusTextBlock.Text = "Re-render cancelled. The existing image was preserved.";
        }
        catch (Exception exception)
        {
            ImageSettingsStatusTextBlock.Text = $"Re-render failed: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_rerenderCancellation, cancellation)) _rerenderCancellation = null;
            RerenderCurrentButton.Content = "Re-render current flame";
        }
    }

    private async void RerenderRated_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRatedRerender is not null)
        {
            _ratedRerenderCancellation?.Cancel();
            return;
        }

        if (_activeRerender is not null)
        {
            ImageSettingsStatusTextBlock.Text = "The current-flame re-render is still running.";
            return;
        }

        _activeRatedRerender = RerenderRatedAsync();
        try { await _activeRatedRerender; }
        catch (Exception exception) { ImageSettingsStatusTextBlock.Text = $"Rated-flame re-render failed: {exception.Message}"; }
        finally { _activeRatedRerender = null; }
    }

    private async Task RerenderRatedAsync()
    {
        EnsureWorkspace();
        var artifacts = _ratingStore!.EnumerateRatedArtifacts();
        if (artifacts.Count == 0)
        {
            ImageSettingsStatusTextBlock.Text = "There are no complete rated PNG/.flame pairs to re-render.";
            return;
        }

        var result = WpfMessageBox.Show(
            this,
            $"Replace {artifacts.Count} rated PNG(s) in ratings/1–5? Star folders and source .flame files will remain unchanged.",
            "Re-render rated flames",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var settings = ReadImageRenderSettings(out var palette);
        using var cancellation = new CancellationTokenSource();
        _ratedRerenderCancellation = cancellation;
        RerenderRatedButton.Content = "Cancel rated re-render";
        _renderControlsLocked = true;
        UpdateRenderActionButtons();
        var workerCount = ParseInt(WorkersTextBox, 1, 1, Math.Max(1, Environment.ProcessorCount));
        var completed = 0;
        try
        {
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellation.Token,
                MaxDegreeOfParallelism = workerCount
            };
            await Parallel.ForEachAsync(artifacts, parallelOptions, async (artifact, token) =>
            {
                Dispatcher.Invoke(() =>
                {
                    ImageSettingsStatusTextBlock.Text = $"Re-rendering rated flames… {completed}/{artifacts.Count} using {workerCount} workers";
                });
                var progress = new ThrottledRenderProgress(Dispatcher, update =>
                {
                    ImageSettingsStatusTextBlock.Text = $"Re-rendering rated flames… {completed}/{artifacts.Count} using {workerCount} workers · {update.CompletedSamples:N0}/{update.TotalSamples:N0} points";
                });
                await _artifactRerenderer.RerenderAsync(artifact, palette, settings, progress, token);
                var finished = Interlocked.Increment(ref completed);
                Dispatcher.Invoke(() => ImageSettingsStatusTextBlock.Text = $"Re-rendering rated flames… {finished}/{artifacts.Count} using {workerCount} workers");
            });

            if (_current is not null && artifacts.Any(artifact => string.Equals(artifact.ImagePath, _current.ImagePath, StringComparison.OrdinalIgnoreCase))) ShowArtifact(_current);
            ImageSettingsStatusTextBlock.Text = $"Re-rendered {completed} rated flame(s) using {workerCount} workers. Ratings and source .flame files were preserved.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ImageSettingsStatusTextBlock.Text = $"Rated re-render cancelled after {completed} of {artifacts.Count}; completed replacements were preserved.";
        }
        finally
        {
            if (ReferenceEquals(_ratedRerenderCancellation, cancellation)) _ratedRerenderCancellation = null;
            _renderControlsLocked = false;
            UpdateRenderActionButtons();
            RerenderRatedButton.Content = "Re-render rated flames";
        }
    }

    private RenderSettings ReadImageRenderSettings(out PaletteDefinition palette)
    {
        palette = (PaletteComboBox.SelectedItem as PaletteDefinition) ?? PaletteDefinition.Monochrome;
        var whitePoint = ParseDouble(WhitePointTextBox, 0, 0, 1);
        var blackPoint = ParseDouble(BlackPointTextBox, 1, 0, 1);
        if (blackPoint <= whitePoint) throw new InvalidOperationException("Black point must be greater than white point.");
        return new RenderSettings
        {
            Width = 2048,
            Height = 2048,
            SampleBudget = ParseInt(SampleBudgetTextBox, 20_000_000, 100, 500_000_000),
            Oversample = ParseInt((OversampleComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), 1, 1, 3),
            FilterRadius = ParseDouble(FilterRadiusTextBox, 0.5, 0, 3),
            Gamma = ParseDouble(GammaTextBox, 1, 0.1, 8),
            Brightness = ParseDouble(BrightnessTextBox, 1, 0.05, 5),
            Vibrancy = ParseDouble(VibrancyTextBox, 1, 0, 1),
            WhitePoint = whitePoint,
            BlackPoint = blackPoint,
            ContrastCurve = ParseDouble(ContrastCurveTextBox, 1, 0.1, 8),
            LowDensityCutoff = ParseDouble(LowDensityCutoffTextBox, 0.01, 0, 1),
            PaletteName = palette.Name
        };
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_renderControlsLocked || !_renderService.Status.IsRunning) return;
        if (_renderService.Status.IsPaused) _renderService.Resume();
        else _renderService.Pause();
        UpdateRenderActionButtons();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_renderControlsLocked) return;
        await _renderService.StopAsync();
        UpdateRenderActionButtons();
    }

    private void StartAi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureWorkspace();
            _aiService.Start(OutputDirectoryTextBox.Text.Trim(), _ratingStore!);
            _aiEnabled = true;
            RefreshCandidates();
            ShowNextReady();
        }
        catch (Exception exception) { WpfMessageBox.Show(this, exception.Message, "Could not start AI scoring", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void StopAi_Click(object sender, RoutedEventArgs e) { await _aiService.StopAsync(); _aiEnabled = false; }

    private async void RescoreRatedDataset_Click(object sender, RoutedEventArgs e)
    {
        if (_activeRatedRescore is not null)
        {
            _ratedRescoreCancellation?.Cancel();
            return;
        }

        if (_activeRerender is not null || _activeRatedRerender is not null)
        {
            AiStatusTextBlock.Text = "Finish image re-rendering before rescoring the rated dataset.";
            return;
        }

        _activeRatedRescore = RescoreRatedDatasetAsync();
        try { await _activeRatedRescore; }
        catch (Exception exception) { AiStatusTextBlock.Text = $"Rated dataset rescore failed: {exception.Message}"; }
        finally { _activeRatedRescore = null; }
    }

    private async Task RescoreRatedDatasetAsync()
    {
        EnsureWorkspace();
        if (_aiService.Status.IsTraining)
        {
            AiStatusTextBlock.Text = "Wait for model training to finish before rescoring the rated dataset.";
            return;
        }

        var ratedCount = _ratingStore!.EnumerateRatedImagePaths().Count;
        if (ratedCount == 0)
        {
            AiStatusTextBlock.Text = "There are no rated PNG images to rescore.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _ratedRescoreCancellation = cancellation;
        RescoreRatedDatasetButton.Content = "Cancel rescore";
        TrainingMetricsTextBlock.Text = $"Rescoring {ratedCount} rated image(s)… rating folders will be preserved.";
        try
        {
            var rescored = await _aiService.RescoreRatedAsync(_ratingStore, cancellation.Token);
            UpdateCurrentText();
            TrainingMetricsTextBlock.Text = $"Rescored {rescored} rated image(s). Rating folders were preserved; score prefixes were updated.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            TrainingMetricsTextBlock.Text = "Rated dataset rescore cancelled. Existing rating folders were preserved.";
        }
        finally
        {
            if (ReferenceEquals(_ratedRescoreCancellation, cancellation)) _ratedRescoreCancellation = null;
            RescoreRatedDatasetButton.Content = "Rescore rated";
        }
    }

    private async void TrainModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureWorkspace();
            var snapshot = PreferenceDatasetBuilder.Snapshot(OutputDirectoryTextBox.Text.Trim());
            UpdateDatasetStatistics(snapshot.Statistics);
            if (!_aiService.Status.IsRunning) _aiService.Start(OutputDirectoryTextBox.Text.Trim(), _ratingStore!);
            _aiEnabled = true;
            TrainingMetricsTextBlock.Text = "Training on the frozen DINOv2 backbone… rendering remains operational.";
            var training = await _aiService.TrainAsync(snapshot, _aiBackend.ModelDirectory);
            UpdateTrainingMetrics(training);
        }
        catch (Exception exception) { WpfMessageBox.Show(this, exception.Message, "Could not train preference model", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void RenderService_ImageReady(RenderedArtifact artifact)
    {
        Dispatcher.Invoke(() =>
        {
            while (_renderService.TryDequeueReady(out _)) { }
            RefreshCandidates();
            if (_current is null) ShowNextReady();
        });
    }

    private void RenderService_RenderFailed(Exception exception) => Dispatcher.Invoke(() => CurrentTextBlock.Text = $"Render failure: {exception.Message}");

    private void AiService_ImageScored(PreferenceScore score)
    {
        Dispatcher.Invoke(() =>
        {
            _catalog.RecordScore(score);
            if (_current is not null && string.Equals(_current.SourceId, score.SourceId, StringComparison.OrdinalIgnoreCase))
            {
                _current = _current with { BaseName = Path.GetFileNameWithoutExtension(score.ImagePath), ImagePath = score.ImagePath };
                ShowArtifact(_current);
            }
            else if (_current is null) ShowNextReady();
        });
    }

    private void AiService_ScoringFailed(Exception exception) => Dispatcher.Invoke(() => AiStatusTextBlock.Text = $"AI scoring error: {exception.Message}");

    private void AiService_TrainingCompleted(TrainingResult result) => Dispatcher.Invoke(() => UpdateTrainingMetrics(result));

    private void ShowArtifact(RenderedArtifact artifact)
    {
        if (!File.Exists(artifact.ImagePath)) return;
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            using (var stream = File.Open(artifact.ImagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            _current = artifact;
            GetSeenSourceIds().Add(artifact.SourceId);
            PreviewImage.Source = bitmap;
            _imagePixelWidth = bitmap.PixelWidth;
            _imagePixelHeight = bitmap.PixelHeight;
            EmptyPreviewTextBlock.Visibility = Visibility.Collapsed;
            UpdateCurrentText();
            if (_fitToViewport) ApplyFitZoom(); else ApplyZoom(_zoomScale);
        }
        catch (Exception exception) { CurrentTextBlock.Text = $"Could not open candidate: {exception.Message}"; }
    }

    private void ShowNextReady()
    {
        RefreshCandidates();
        var seenSourceIds = GetSeenSourceIds();
        var next = _current is null
            ? _catalog.FirstUnseen(_aiEnabled, seenSourceIds)
            : _catalog.NextUnseen(_current.SourceId, _aiEnabled, seenSourceIds);
        if (_deferredAfterUndo is { } deferred)
        {
            _deferredAfterUndo = null;
            ShowArtifact(deferred);
        }
        else if (next is not null) ShowArtifact(next);
        else
        {
            _current = null;
            PreviewImage.Source = null;
            EmptyPreviewTextBlock.Visibility = Visibility.Visible;
            CurrentTextBlock.Text = string.Empty;
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        RefreshCandidates();
        if (_current is not null && _catalog.Adjacent(_current.SourceId, -1, _aiEnabled) is { } previous) ShowArtifact(previous);
    }

    private void Next_Click(object sender, RoutedEventArgs e) => ShowNextReady();

    private void Rating_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null || _ratingStore is null) return;
        var rating = int.Parse(((WpfButton)sender).Tag.ToString()!, CultureInfo.InvariantCulture);
        try
        {
            var current = ResolveCurrentArtifact();
            _ratingStore.Rate(current.ImagePath, rating, CopyRatedFilesCheckBox.IsChecked == true);
            _lastRated = current;
            RefreshWorkspaceStatistics();
            ShowNextReady();
        }
        catch (Exception exception) { WpfMessageBox.Show(this, exception.Message, "Could not save rating", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => ShowNextReady();

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRated is null || _ratingStore?.Undo() != true) return;
        _deferredAfterUndo = _current;
        ShowArtifact(_lastRated);
        _lastRated = null;
        RefreshWorkspaceStatistics();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e) { _fitToViewport = true; ApplyFitZoom(); }
    private void ActualSize_Click(object sender, RoutedEventArgs e) { _fitToViewport = false; ApplyZoom(1); }

    private void PreviewScroll_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_current is null || _imagePixelWidth <= 0 || _imagePixelHeight <= 0) return;
        _fitToViewport = false;
        var pointer = e.GetPosition(PreviewScroll);
        ApplyZoom(_zoomScale * (e.Delta > 0 ? 1.2 : 1 / 1.2), pointer);
        e.Handled = true;
    }

    private void PreviewScroll_SizeChanged(object sender, SizeChangedEventArgs e) { if (_fitToViewport && _current is not null) ApplyFitZoom(); }

    private void ApplyFitZoom()
    {
        if (_imagePixelWidth <= 0 || _imagePixelHeight <= 0) return;
        var scale = Math.Min(Math.Max(1, PreviewScroll.ViewportWidth) / _imagePixelWidth, Math.Max(1, PreviewScroll.ViewportHeight) / _imagePixelHeight);
        ApplyZoom(Math.Clamp(scale, 0.05, 8));
    }

    private void ApplyZoom(double requestedScale, WpfPoint? anchor = null)
    {
        if (_applyingZoom || _imagePixelWidth <= 0 || _imagePixelHeight <= 0) return;
        _applyingZoom = true;
        try
        {
            var oldScale = Math.Max(0.05, _zoomScale);
            var anchorPoint = anchor ?? new WpfPoint(PreviewScroll.ViewportWidth / 2, PreviewScroll.ViewportHeight / 2);
            var imageOrigin = PreviewImage.TranslatePoint(new WpfPoint(0, 0), PreviewScroll);
            var imageX = Math.Clamp((anchorPoint.X - imageOrigin.X) / oldScale, 0, _imagePixelWidth);
            var imageY = Math.Clamp((anchorPoint.Y - imageOrigin.Y) / oldScale, 0, _imagePixelHeight);
            _zoomScale = Math.Clamp(requestedScale, 0.05, 8);
            PreviewImage.Width = _imagePixelWidth * _zoomScale;
            PreviewImage.Height = _imagePixelHeight * _zoomScale;
            PreviewImage.Stretch = Stretch.Fill;
            PreviewScroll.UpdateLayout();
            if (anchor is not null)
            {
                var newOrigin = PreviewImage.TranslatePoint(new WpfPoint(0, 0), PreviewScroll);
                PreviewScroll.ScrollToHorizontalOffset(PreviewScroll.HorizontalOffset + newOrigin.X - (anchorPoint.X - imageX * _zoomScale));
                PreviewScroll.ScrollToVerticalOffset(PreviewScroll.VerticalOffset + newOrigin.Y - (anchorPoint.Y - imageY * _zoomScale));
            }
        }
        finally { _applyingZoom = false; }
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { SelectedPath = OutputDirectoryTextBox.Text };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) OutputDirectoryTextBox.Text = dialog.SelectedPath;
    }

    private void Window_KeyDown(object sender, WpfKeyEventArgs e)
    {
        var rating = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            _ => 0
        };
        if (rating > 0) { Rating_Click(new WpfButton { Tag = rating }, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Left) { Previous_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Right) { Next_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.U) { Undo_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.P) { Pause_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { Stop_Click(sender, e); e.Handled = true; }
    }

    private void UpdateStatus(object? sender, EventArgs e)
    {
        var status = _renderService.Status;
        UpdateRenderActionButtons();
        QueueTextBlock.Text = $"Queue: {status.QueueDepth}/{status.QueueCapacity} · ready {status.ReadyCount}\nCompleted: {status.Completed} · failures: {status.Failed}";
        var sampleProgress = status.ActiveSampleBudget > 0 ? $" · samples {status.ActiveSamples:N0}/{status.ActiveSampleBudget:N0}" : string.Empty;
        SessionTextBlock.Text = status.IsRunning ? $"Session: {(status.IsPaused ? "PAUSED" : "RUNNING")} · {status.Elapsed:hh\\:mm\\:ss} · limit {status.BatchLimit}{sampleProgress}" : "Session: idle";
        var ai = _aiService.Status;
        AiStatusTextBlock.Text = $"AI: {(ai.IsRunning ? (ai.IsPaused ? "PAUSED" : "RUNNING") : "idle")} · pending {ai.PendingImages} · scored {ai.ScoredImages} · failures {ai.Failed} · progress {ai.Completed}/{ai.Total}\nModel: {ai.ModelVersion ?? "not trained"} · device {ai.Diagnostics.ActiveDevice}";
    }

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= UpdateStatus;
        _rerenderCancellation?.Cancel();
        _ratedRerenderCancellation?.Cancel();
        _ratedRescoreCancellation?.Cancel();
        if (_activeRerender is not null)
        {
            try { await _activeRerender; } catch (Exception) { }
        }
        if (_activeRatedRerender is not null)
        {
            try { await _activeRatedRerender; } catch (Exception) { }
        }
        if (_activeRatedRescore is not null)
        {
            try { await _activeRatedRescore; } catch (Exception) { }
        }
        await _renderService.DisposeAsync();
        await _aiService.DisposeAsync();
        Close();
    }

    private void EnsureWorkspace()
    {
        var output = Path.GetFullPath(OutputDirectoryTextBox.Text.Trim());
        if (_archive is null || _ratingStore is null || !string.Equals(_archive.RootDirectory, output, StringComparison.OrdinalIgnoreCase))
        {
            _archive = new SourceArchive(output);
            _ratingStore = new RatingStore(output);
        }
        RefreshCandidates();
        RefreshWorkspaceStatistics();
    }

    private void RefreshCandidates()
    {
        if (_archive is null || _ratingStore is null) return;
        _catalog.Refresh(_archive, _ratingStore);
    }

    private HashSet<string> GetSeenSourceIds()
    {
        var workspace = _archive?.RootDirectory ?? Path.GetFullPath(OutputDirectoryTextBox.Text.Trim());
        if (!_seenSourceIdsByWorkspace.TryGetValue(workspace, out var seenSourceIds))
        {
            seenSourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _seenSourceIdsByWorkspace[workspace] = seenSourceIds;
        }
        return seenSourceIds;
    }

    private void RefreshWorkspaceStatistics()
    {
        if (_ratingStore is null)
        {
            RatingCountTextBlock.Text = "Rated: 0";
            return;
        }

        var statistics = PreferenceDatasetBuilder.Snapshot(_ratingStore.RootDirectory).Statistics;
        RatingCountTextBlock.Text = $"Rated: {statistics.Total} · PNG/.flame pairs: {_ratingStore.RatingFoldersContainPairedFiles()}";
        UpdateDatasetStatistics(statistics);
    }

    private RenderedArtifact ResolveCurrentArtifact()
    {
        if (_current is not null && File.Exists(_current.ImagePath)) return _current;
        RefreshCandidates();
        return _catalog.Ordered(_aiEnabled).FirstOrDefault(artifact => string.Equals(artifact.SourceId, _current?.SourceId, StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException("The current candidate is no longer available.");
    }

    private void UpdateCurrentText()
    {
        if (_current is null) return;
        var score = _aiService.TryGetScore(_current.SourceId, out var scored) ? scored : _catalog.GetScore(_current);
        var rating = _ratingStore?.FindRating(_current.ImagePath);
        var scoreText = score is null ? "AI score: pending" : $"AI score: {score.Score:0.00000} · expected rating {score.ExpectedRating:0.00}";
        var ratingText = rating is null ? string.Empty : $" · human rating: {rating}★";
        CurrentTextBlock.Text = $"{_current.BaseName}\nSource ID: {_current.SourceId}\n{scoreText} · model: {score?.ModelVersion ?? _aiService.Status.ModelVersion ?? "none"}{ratingText}";
    }

    private void UpdateAiDiagnostics(DeviceDiagnostics diagnostics)
    {
        AiDiagnosticsTextBlock.Text = $"Python: {(diagnostics.PythonAvailable ? "available" : "unavailable")} · PyTorch: {diagnostics.PyTorchVersion} · CUDA: {(diagnostics.CudaAvailable ? "available" : "unavailable")}\nGPU: {diagnostics.GpuName} · active device: {diagnostics.ActiveDevice}\n{diagnostics.Detail}";
        AiStatusTextBlock.Text = diagnostics.AiReady ? "AI is ready; train a model to begin preference scoring." : "AI scoring/training disabled; manual rendering and rating remain available.";
    }

    private void UpdateRenderActionButtons()
    {
        if (_renderControlsLocked)
        {
            StartStopRenderButton.IsEnabled = false;
            PauseResumeRenderButton.IsEnabled = false;
            RandomGeneratorSettingsButton.IsEnabled = false;
            return;
        }

        var status = _renderService.Status;
        StartStopRenderButton.Content = status.IsRunning ? "Stop" : "Start";
        PauseResumeRenderButton.Content = status.IsPaused ? "Resume" : "Pause";
        StartStopRenderButton.IsEnabled = true;
        PauseResumeRenderButton.IsEnabled = status.IsRunning;
        RandomGeneratorSettingsButton.IsEnabled = !status.IsRunning;
    }

    private void UpdateDatasetStatistics(DatasetStatistics statistics)
    {
        var maximum = Math.Max(1, statistics.Counts.Values.Max());
        RatingCount1.Text = statistics.CountFor(1).ToString(CultureInfo.InvariantCulture);
        RatingCount2.Text = statistics.CountFor(2).ToString(CultureInfo.InvariantCulture);
        RatingCount3.Text = statistics.CountFor(3).ToString(CultureInfo.InvariantCulture);
        RatingCount4.Text = statistics.CountFor(4).ToString(CultureInfo.InvariantCulture);
        RatingCount5.Text = statistics.CountFor(5).ToString(CultureInfo.InvariantCulture);
        RatingBar1.Width = 220d * statistics.CountFor(1) / maximum;
        RatingBar2.Width = 220d * statistics.CountFor(2) / maximum;
        RatingBar3.Width = 220d * statistics.CountFor(3) / maximum;
        RatingBar4.Width = 220d * statistics.CountFor(4) / maximum;
        RatingBar5.Width = 220d * statistics.CountFor(5) / maximum;
        var readinessBrush = statistics.Readiness switch
        {
            DatasetReadinessColor.Red => WpfBrushes.IndianRed,
            DatasetReadinessColor.Amber => WpfBrushes.Goldenrod,
            _ => WpfBrushes.MediumSeaGreen
        };
        RatingBar1.Background = readinessBrush;
        RatingBar2.Background = readinessBrush;
        RatingBar3.Background = readinessBrush;
        RatingBar4.Background = readinessBrush;
        RatingBar5.Background = readinessBrush;
        DatasetReadinessTextBlock.Foreground = readinessBrush;
        DatasetReadinessTextBlock.Text = $"Total: {statistics.Total} · readiness: {statistics.Readiness}\n{statistics.ReadinessMessage}\nReadiness colors are heuristics, not scientific guarantees.";
        TrainingWarningTextBlock.Text = statistics.Readiness == DatasetReadinessColor.Green ? string.Empty : "Small/imbalanced corpus warning: training is available, but held-out metrics may be unreliable.";
    }

    private void UpdateTrainingMetrics(TrainingResult result)
    {
        var metrics = result.Metrics;
        var controls = metrics.Controls.Count == 0 ? "none" : string.Join(", ", metrics.Controls.Select(control => $"{control.Name} {control.Score:0.000}"));
        TrainingMetricsTextBlock.Text = $"Model {result.ModelVersion}\nOrdinal accuracy {metrics.OrdinalAccuracy:0.000} · MAE {metrics.MeanAbsoluteRatingError:0.000}\nSpearman/rank {metrics.SpearmanCorrelation:0.000} · calibration error {metrics.CalibrationError:0.000}\nEvaluation: {(metrics.IsReliable ? "usable split" : "unreliable: corpus too small for meaningful validation/test")}\nControls (evaluation only): {controls}";
    }

    private static int ParseInt(WpfTextBox box, int fallback, int minimum, int maximum) => ParseInt(box.Text, fallback, minimum, maximum);
    private static int ParseInt(string? value, int fallback, int minimum, int maximum) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;
    private static long ParseLong(WpfTextBox box, long fallback) => long.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    private static double ParseDouble(WpfTextBox box, double fallback, double minimum, double maximum) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;

    private sealed class ThrottledRenderProgress(Dispatcher dispatcher, Action<RenderProgress> report) : IProgress<RenderProgress>
    {
        private readonly object _gate = new();
        private long _nextUpdate;

        public void Report(RenderProgress value)
        {
            lock (_gate)
            {
                var now = Environment.TickCount64;
                if (value.CompletedSamples < value.TotalSamples && now < _nextUpdate) return;
                _nextUpdate = now + 250;
            }
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => report(value)));
        }
    }
}
