using System.Diagnostics;
using System.Text.Json;
using FractalFlameCurator.Models;

namespace FractalFlameCurator.Ai;

public interface IPreferenceScoringBackend : IAsyncDisposable
{
    DeviceDiagnostics Diagnostics { get; }
    string? ActiveModelVersion { get; }
    Task<DeviceDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PreferenceScore>> ScoreAsync(IReadOnlyList<string> imagePaths, CancellationToken cancellationToken);
    Task<TrainingResult> TrainAsync(DatasetSnapshot snapshot, string modelDirectory, CancellationToken cancellationToken);
}

public sealed record DinoV2BackendOptions
{
    public string PythonExecutable { get; init; } = OperatingSystem.IsWindows() ? "py" : "python3";
    public string? ScriptPath { get; init; }
    public string ModelDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FractalFlameCurator", "models");
}

public sealed class DinoV2PreferenceBackend : IPreferenceScoringBackend
{
    private readonly DinoV2BackendOptions _options;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private DeviceDiagnostics _diagnostics = DeviceDiagnostics.Unavailable("DINOv2 diagnostics have not been queried yet.");
    private string? _activeModelVersion;

    public DinoV2PreferenceBackend(DinoV2BackendOptions? options = null) => _options = options ?? new DinoV2BackendOptions();

    public DeviceDiagnostics Diagnostics => _diagnostics;
    public string? ActiveModelVersion => _activeModelVersion;
    public string ModelDirectory => _options.ModelDirectory;

    public async Task<DeviceDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await SendAsync<DinoDiagnosticsResponse>(new { command = "diagnostics", model_directory = _options.ModelDirectory }, cancellationToken);
            _diagnostics = new DeviceDiagnostics(true, response.TorchAvailable, response.CudaAvailable, response.GpuName, response.PyTorchVersion, response.ActiveDevice, response.AiReady, response.Detail);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or JsonException)
        {
            _diagnostics = DeviceDiagnostics.Unavailable($"DINOv2 is unavailable: {exception.Message}");
        }
        return _diagnostics;
    }

    public async Task<IReadOnlyList<PreferenceScore>> ScoreAsync(IReadOnlyList<string> imagePaths, CancellationToken cancellationToken)
    {
        if (!_diagnostics.AiReady) throw new InvalidOperationException("AI scoring is disabled because CUDA and a usable PyTorch DINOv2 runtime are unavailable.");
        var response = await SendAsync<DinoScoreResponse>(new { command = "score", paths = imagePaths, model_directory = _options.ModelDirectory }, cancellationToken);
        _activeModelVersion = response.ModelVersion;
        return response.Scores.Select(score => new PreferenceScore(score.Path, CandidateFileNaming.GetSourceId(Path.GetFileName(score.Path)), Math.Clamp(score.Score, 0, 1), Math.Clamp(score.ExpectedRating, 1, 5), response.ModelVersion)).ToArray();
    }

    public async Task<TrainingResult> TrainAsync(DatasetSnapshot snapshot, string modelDirectory, CancellationToken cancellationToken)
    {
        if (!_diagnostics.AiReady) throw new InvalidOperationException("AI training is disabled because CUDA and a usable PyTorch DINOv2 runtime are unavailable.");
        Directory.CreateDirectory(modelDirectory);
        var request = new
        {
            command = "train",
            model_directory = modelDirectory,
            images = snapshot.Images.Select(image => new { path = image.ImagePath, image.Rating, source_id = image.SourceId }),
            train = snapshot.Split.Train.Select(ToRequestImage),
            validation = snapshot.Split.Validation.Select(ToRequestImage),
            test = snapshot.Split.Test.Select(ToRequestImage),
            controls = PreferenceDatasetBuilder.ControlImages(snapshot.RootDirectory).Select(control => new { name = control.Name, path = control.ImagePath })
        };
        var response = await SendAsync<DinoTrainingResponse>(request, cancellationToken);
        _activeModelVersion = response.ModelVersion;
        return new TrainingResult(response.ModelVersion, snapshot.Statistics, snapshot.Split, new TrainingMetrics(response.Metrics.OrdinalAccuracy, response.Metrics.MeanAbsoluteRatingError, response.Metrics.SpearmanCorrelation, response.Metrics.RankCorrelation, response.Metrics.CalibrationError, response.Metrics.IsReliable, response.Metrics.Controls.Select(control => new ControlMetric(control.Name, control.Score, control.Interpretation)).ToArray()));
    }

    public async ValueTask DisposeAsync()
    {
        await _ioGate.WaitAsync();
        try
        {
            if (_input is not null)
            {
                try { await _input.WriteLineAsync(JsonSerializer.Serialize(new { command = "shutdown" }, _jsonOptions)); } catch (IOException) { }
            }
            if (_process is { HasExited: false })
            {
                using var waitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _process.WaitForExitAsync(waitCancellation.Token); }
                catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(true); }
            }
            _process?.Dispose();
            _process = null;
            _input = null;
            _output = null;
        }
        finally { _ioGate.Release(); _ioGate.Dispose(); }
    }

    private async Task<T> SendAsync<T>(object request, CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            await _input!.WriteLineAsync(JsonSerializer.Serialize(request, _jsonOptions));
            await _input.FlushAsync(cancellationToken);
            var line = await _output!.ReadLineAsync(cancellationToken) ?? throw new IOException("The DINOv2 worker closed its output stream.");
            var error = JsonSerializer.Deserialize<DinoErrorResponse>(line, _jsonOptions);
            if (!string.IsNullOrWhiteSpace(error?.Error)) throw new InvalidOperationException(error.Error);
            return JsonSerializer.Deserialize<T>(line, _jsonOptions) ?? throw new JsonException("The DINOv2 worker returned an empty response.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            StopWorker();
            throw;
        }
        finally { _ioGate.Release(); }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;
        var script = _options.ScriptPath ?? Path.Combine(AppContext.BaseDirectory, "Ai", "dinov2_service.py");
        if (!File.Exists(script)) throw new FileNotFoundException("The bundled DINOv2 worker script was not found.", script);
        var startInfo = new ProcessStartInfo(_options.PythonExecutable)
        {
            WorkingDirectory = Path.GetDirectoryName(script) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(script);
        if (OperatingSystem.IsWindows() && string.Equals(Path.GetFileNameWithoutExtension(_options.PythonExecutable), "py", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Insert(0, "-3.12");
        }
        startInfo.ArgumentList.Add("--server");
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the DINOv2 Python worker.");
        _input = _process.StandardInput;
        _output = _process.StandardOutput;
        _ = DrainErrorsAsync(_process.StandardError);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void StopWorker()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(true); } catch (InvalidOperationException) { }
        }
        _process?.Dispose();
        _process = null;
        _input = null;
        _output = null;
    }

    private static object ToRequestImage(DatasetImage image) => new { path = image.ImagePath, rating = image.Rating, source_id = image.SourceId };

    private static async Task DrainErrorsAsync(StreamReader errors)
    {
        try { while (await errors.ReadLineAsync() is not null) { } } catch (IOException) { }
    }

    private sealed record DinoDiagnosticsResponse(bool TorchAvailable, bool CudaAvailable, string GpuName, string PyTorchVersion, string ActiveDevice, bool AiReady, string Detail);
    private sealed record DinoErrorResponse(string? Error);
    private sealed record DinoScoreItem(string Path, double Score, double ExpectedRating);
    private sealed record DinoScoreResponse(string ModelVersion, IReadOnlyList<DinoScoreItem> Scores);
    private sealed record DinoControlMetric(string Name, double? Score, string Interpretation);
    private sealed record DinoTrainingMetrics(double OrdinalAccuracy, double MeanAbsoluteRatingError, double SpearmanCorrelation, double RankCorrelation, double CalibrationError, bool IsReliable, IReadOnlyList<DinoControlMetric> Controls);
    private sealed record DinoTrainingResponse(string ModelVersion, DinoTrainingMetrics Metrics);
}
