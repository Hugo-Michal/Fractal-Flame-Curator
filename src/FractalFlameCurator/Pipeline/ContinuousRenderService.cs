using System.Collections.Concurrent;
using System.Threading.Channels;
using FractalFlameCurator.Generation;
using FractalFlameCurator.Models;
using FractalFlameCurator.Rendering;
using FractalFlameCurator.Storage;

namespace FractalFlameCurator.Pipeline;

public sealed record ContinuousRenderOptions
{
    public string OutputDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ApophysisCurator");
    public int BatchLimit { get; init; } = 100;
    public int QueueCapacity { get; init; } = 4;
    public int WorkerCount { get; init; } = Math.Max(1, Math.Min(4, Environment.ProcessorCount));
    public long Seed { get; init; } = DateTime.UtcNow.Ticks;
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public RenderSettings RenderSettings { get; init; } = new();
    public PaletteDefinition Palette { get; init; } = PaletteDefinition.Monochrome;
    public RandomGeneratorSettings GeneratorSettings { get; init; } = RandomGeneratorSettings.CreateDefault();
}

public sealed record ContinuousRenderStatus(
    bool IsRunning,
    bool IsPaused,
    int QueueDepth,
    int QueueCapacity,
    int ReadyCount,
    int Completed,
    int Failed,
    int BatchLimit,
    TimeSpan Elapsed,
    int ActiveSamples,
    int ActiveSampleBudget);

public sealed class ContinuousRenderService : IAsyncDisposable
{
    private readonly FlameGenerator _generator;
    private readonly IFlameRenderer _renderer;
    private readonly ConcurrentQueue<RenderedArtifact> _ready = new();
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _renderEnabled = new(initialState: true);
    private BoundedRenderQueue<RenderJob>? _jobs;
    private CancellationTokenSource? _cancellation;
    private Task[] _workers = [];
    private Task? _producer;
    private ContinuousRenderOptions? _options;
    private TaskCompletionSource<bool> _resume = CompletedSignal();
    private DateTime _started;
    private int _completed;
    private int _failed;
    private int _activeSamples;
    private int _activeSampleBudget;

    public ContinuousRenderService(FlameGenerator generator, IFlameRenderer renderer)
    {
        _generator = generator;
        _renderer = renderer;
    }

    public event Action<RenderedArtifact>? ImageReady;
    public event Action<Exception>? RenderFailed;

    public ContinuousRenderStatus Status
    {
        get
        {
            var options = _options;
            var sessionRunning = _cancellation is not null &&
                !_cancellation.IsCancellationRequested &&
                (_producer is { IsCompleted: false } || _workers.Any(worker => !worker.IsCompleted));
            return new ContinuousRenderStatus(
                sessionRunning,
                !_resume.Task.IsCompleted,
                _jobs?.Count ?? 0,
                options?.QueueCapacity ?? 0,
                _ready.Count,
                Volatile.Read(ref _completed),
                Volatile.Read(ref _failed),
                options?.BatchLimit ?? 0,
                _started == default ? TimeSpan.Zero : DateTime.UtcNow - _started,
                Volatile.Read(ref _activeSamples),
                Volatile.Read(ref _activeSampleBudget));
        }
    }

    public void Start(ContinuousRenderOptions options)
    {
        lock (_gate)
        {
            if (_cancellation is not null && _producer is { IsCompleted: true } && _workers.All(worker => worker.IsCompleted))
            {
                _cancellation.Dispose();
                _cancellation = null;
                _jobs = null;
                _producer = null;
                _workers = [];
            }
            if (_cancellation is not null) throw new InvalidOperationException("A render session is already running.");
            _options = options with
            {
                BatchLimit = Math.Clamp(options.BatchLimit, 1, 1_000_000),
                QueueCapacity = Math.Clamp(options.QueueCapacity, 1, 64),
                WorkerCount = Math.Clamp(options.WorkerCount, 1, Math.Max(1, Environment.ProcessorCount))
            };
            _cancellation = new CancellationTokenSource();
            _resume = CompletedSignal();
            _renderEnabled.Set();
            _jobs = new BoundedRenderQueue<RenderJob>(_options.QueueCapacity);
            _started = DateTime.UtcNow;
            _completed = 0;
            _failed = 0;
            _activeSamples = 0;
            _activeSampleBudget = 0;
            _producer = ProduceAsync(_cancellation.Token);
            _workers = Enumerable.Range(0, _options.WorkerCount).Select(_ => ConsumeAsync(_cancellation.Token)).ToArray();
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_cancellation is null) return;
            if (_resume.Task.IsCompleted) _resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _renderEnabled.Reset();
        }
    }

    public void Resume()
    {
        _renderEnabled.Set();
        _resume.TrySetResult(true);
    }

    public async Task StopAsync()
    {
        Task[] tasks;
        lock (_gate)
        {
            if (_cancellation is null) return;
            _cancellation.Cancel();
            _renderEnabled.Set();
            _jobs?.Complete();
            _resume.TrySetResult(true);
            tasks = _workers.Append(_producer).Where(task => task is not null).Cast<Task>().ToArray();
        }
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        lock (_gate)
        {
            _cancellation.Dispose();
            _cancellation = null;
            _jobs = null;
            _producer = null;
            _workers = [];
        }
    }

    public bool TryDequeueReady(out RenderedArtifact artifact) => _ready.TryDequeue(out artifact!);

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _renderEnabled.Dispose();
    }

    private async Task ProduceAsync(CancellationToken cancellationToken)
    {
        var options = _options!;
        try
        {
            for (var i = 0; i < options.BatchLimit; i++)
            {
                await WaitIfPaused(cancellationToken);
                var seed = unchecked(options.Seed + i * 0x9E3779B9L);
                await _jobs!.EnqueueAsync(new RenderJob(i + 1, seed), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally { _jobs?.Complete(); }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        var options = _options!;
        while (!cancellationToken.IsCancellationRequested)
        {
            RenderJob job;
            try { job = await _jobs!.DequeueAsync(cancellationToken); }
            catch (ChannelClosedException) { return; }
            catch (OperationCanceledException) { return; }
            try
            {
                await WaitIfPaused(cancellationToken);
                var genome = _generator.Generate(job.Seed, new FlameGeneratorOptions
                {
                    Width = options.RenderSettings.Width,
                    Height = options.RenderSettings.Height,
                    Palette = options.Palette,
                    GeneratorSettings = options.GeneratorSettings
                });
                genome.Quality = options.RenderSettings.SampleBudget;
                genome.Oversample = options.RenderSettings.Oversample;
                genome.FilterRadius = options.RenderSettings.FilterRadius;
                genome.Gamma = options.RenderSettings.Gamma;
                genome.Brightness = options.RenderSettings.Brightness;
                genome.Vibrancy = options.RenderSettings.Vibrancy;
                var progress = new RenderProgressReporter(_renderEnabled, cancellationToken, update =>
                {
                    Volatile.Write(ref _activeSamples, update.CompletedSamples);
                    Volatile.Write(ref _activeSampleBudget, update.TotalSamples);
                });
                var frame = await _renderer.RenderAsync(genome, options.RenderSettings, progress, cancellationToken);
                Volatile.Write(ref _activeSamples, 0);
                Volatile.Write(ref _activeSampleBudget, 0);
                var archive = new SourceArchive(options.OutputDirectory);
                var artifact = archive.Save(genome, frame, job.Sequence, options.SessionId);
                _ready.Enqueue(artifact);
                Interlocked.Increment(ref _completed);
                ImageReady?.Invoke(artifact);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                Volatile.Write(ref _activeSamples, 0);
                Volatile.Write(ref _activeSampleBudget, 0);
                Interlocked.Increment(ref _failed);
                RenderFailed?.Invoke(exception);
            }
        }
    }

    private Task WaitIfPaused(CancellationToken cancellationToken) => _resume.Task.WaitAsync(cancellationToken);
    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult(true);
        return signal;
    }

    private sealed record RenderJob(int Sequence, long Seed);

    private sealed class RenderProgressReporter(ManualResetEventSlim renderEnabled, CancellationToken cancellationToken, Action<RenderProgress> report) : IProgress<RenderProgress>
    {
        public void Report(RenderProgress value)
        {
            renderEnabled.Wait(cancellationToken);
            report(value);
        }
    }
}
