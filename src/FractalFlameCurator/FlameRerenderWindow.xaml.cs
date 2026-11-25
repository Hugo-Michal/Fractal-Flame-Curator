using System.Globalization;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using FractalFlameCurator.Controls;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfMouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using WpfPoint = System.Windows.Point;
using FractalFlameCurator.Models;
using FractalFlameCurator.Rendering;
using FractalFlameCurator.Serialization;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace FractalFlameCurator;

public partial class FlameRerenderWindow : Window
{
    private readonly FlameRenderSession _session;
    private readonly ObservableCollection<string> _flamePaths = [];
    private bool _updatingQueueSelection;
    private FlameGenome? _genome;
    private RenderedFrame16? _frame;
    private CancellationTokenSource? _cancellation;
    private double _zoomScale = 1;
    private bool _fitToViewport = true;
    private bool _applyingZoom;

    public FlameRerenderWindow(IHistogramFlameRenderer renderer, RenderSettings settings, PaletteDefinition palette)
    {
        InitializeComponent();
        _session = new FlameRenderSession(renderer);
        FlameQueueList.ItemsSource = _flamePaths;
        SizeTextBox.Text = settings.Width.ToString(CultureInfo.InvariantCulture);
        GroupedIntegerInput.Attach(SizeTextBox);
        WorkersTextBox.Text = Math.Max(1, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture);
        SamplesTextBox.Text = settings.SampleBudget.ToString(CultureInfo.InvariantCulture);
        GroupedIntegerInput.Attach(SamplesTextBox);
        OversampleComboBox.SelectedIndex = Math.Clamp(settings.Oversample, 1, 6) - 1;
        FilterTextBox.Text = Format(settings.FilterRadius);
        SmoothingTextBox.Text = "0";
        VibrancyTextBox.Text = Format(settings.Vibrancy);
        CutoffTextBox.Text = Format(settings.LowDensityCutoff);
        PaletteComboBox.ItemsSource = new[] { "Use source palette" }.Concat(PaletteDefinition.BuiltIns.Select(p => p.Name)).ToArray();
        PaletteComboBox.SelectedItem = palette.Name;
        if (PaletteComboBox.SelectedIndex < 0) PaletteComboBox.SelectedIndex = 0;
        foreach (var box in new[] { CenterXTextBox, CenterYTextBox, ScaleTextBox, RotationTextBox, SizeTextBox,
                     WorkersTextBox, SamplesTextBox, FilterTextBox, SmoothingTextBox, VibrancyTextBox, CutoffTextBox })
            box.TextChanged += (_, _) => UpdateRenderMode();
        OversampleComboBox.SelectionChanged += (_, _) => UpdateRenderMode();
        PaletteComboBox.SelectionChanged += (_, _) => UpdateRenderMode();
        StableToneCheckBox.Checked += (_, _) => UpdateRenderMode();
        StableToneCheckBox.Unchecked += (_, _) => UpdateRenderMode();
        CurveEditor.CurveChanged += (_, _) => { UpdateRenderMode(); UpdateHistogramSummary(); };
        ShowOutputHistogramCheckBox.Checked += (_, _) => CurveEditor.ShowOutputHistogram = true;
        ShowOutputHistogramCheckBox.Unchecked += (_, _) => CurveEditor.ShowOutputHistogram = false;
        LogHistogramCheckBox.Checked += (_, _) => CurveEditor.UseLogarithmicHistogram = true;
        LogHistogramCheckBox.Unchecked += (_, _) => CurveEditor.UseLogarithmicHistogram = false;
        AutomaticCurveCheckBox.Checked += (_, _) => AutomaticCurveChanged();
        AutomaticCurveCheckBox.Unchecked += (_, _) => AutomaticCurveChanged();
        UpdateHistogramSummary();
    }

    private void Load_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Apophysis flame files (*.flame)|*.flame|All files (*.*)|*.*", CheckFileExists = true, Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var path in dialog.FileNames)
            if (!_flamePaths.Contains(path, StringComparer.OrdinalIgnoreCase)) _flamePaths.Add(path);
        QueueExpander.Header = $"Flame queue ({_flamePaths.Count})";
        if (_flamePaths.Count > 1) QueueExpander.IsExpanded = true;
        if (FlameQueueList.SelectedIndex < 0 && _flamePaths.Count > 0) FlameQueueList.SelectedIndex = 0;
        UpdateRenderMode();
    }

    private void FlameQueue_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingQueueSelection || FlameQueueList.SelectedItem is not string path ||
            (_genome is not null && string.Equals(FlamePathTextBox.Text, path, StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            _session.Clear();
            _genome = FlameXmlSerializer.Load(path);
            FlamePathTextBox.Text = path;
            CenterXTextBox.Text = Format(_genome.CenterX);
            CenterYTextBox.Text = Format(_genome.CenterY);
            ScaleTextBox.Text = Format(_genome.Scale);
            RotationTextBox.Text = Format(_genome.Rotate);
            _frame = null;
            PreviewImage.Source = null;
            PreviewImage.ClearValue(FrameworkElement.WidthProperty);
            PreviewImage.ClearValue(FrameworkElement.HeightProperty);
            CurveEditor.Histogram = null;
            CurveEditor.OutputHistogram = null;
            UpdateHistogramSummary();
            _fitToViewport = true;
            EmptyPreviewText.Visibility = Visibility.Visible;
            RenderButton.IsEnabled = true;
            SaveButton.IsEnabled = false;
            StatusText.Text = $"Loaded {_genome.Name}. Adjust settings and render.";
            UpdateRenderMode();
        }
        catch (Exception exception)
        {
            _session.Clear();
            _genome = null;
            _frame = null;
            PreviewImage.Source = null;
            PreviewImage.ClearValue(FrameworkElement.WidthProperty);
            PreviewImage.ClearValue(FrameworkElement.HeightProperty);
            CurveEditor.Histogram = null;
            CurveEditor.OutputHistogram = null;
            UpdateHistogramSummary();
            _fitToViewport = true;
            EmptyPreviewText.Visibility = Visibility.Visible;
            RenderButton.IsEnabled = false;
            SaveButton.IsEnabled = false;
            StatusText.Text = $"Could not load flame: {exception.Message}";
            UpdateRenderMode();
        }
    }

    private void QueueMoveUp_Click(object sender, RoutedEventArgs e) => MoveQueueItem(-1);
    private void QueueMoveDown_Click(object sender, RoutedEventArgs e) => MoveQueueItem(1);

    private void MoveQueueItem(int offset)
    {
        var index = FlameQueueList.SelectedIndex;
        var next = index + offset;
        if (index < 0 || next < 0 || next >= _flamePaths.Count) return;
        _updatingQueueSelection = true;
        try { _flamePaths.Move(index, next); FlameQueueList.SelectedIndex = next; }
        finally { _updatingQueueSelection = false; }
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        var index = FlameQueueList.SelectedIndex;
        if (index < 0) return;
        _flamePaths.RemoveAt(index);
        QueueExpander.Header = $"Flame queue ({_flamePaths.Count})";
        if (_flamePaths.Count > 0) FlameQueueList.SelectedIndex = Math.Min(index, _flamePaths.Count - 1);
        else
        {
            _session.Clear(); _genome = null; _frame = null;
            FlamePathTextBox.Text = string.Empty; PreviewImage.Source = null;
            CurveEditor.Histogram = null; CurveEditor.OutputHistogram = null;
            EmptyPreviewText.Visibility = Visibility.Visible; SaveButton.IsEnabled = false; RenderButton.IsEnabled = false;
            UpdateHistogramSummary();
        }
        UpdateRenderMode();
    }

    private async void Render_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null) { _cancellation.Cancel(); return; }
        if (_genome is null) return;

        RenderRequest request;
        FlameRenderChange change;
        try
        {
            request = ReadRequest();
            change = _flamePaths.Count > 1 ? FlameRenderChange.Sampling :
                _session.GetChange(request.Genome, request.Settings, request.Smoothing, request.Curve, request.AutomaticCurve);
            if (change == FlameRenderChange.None && _flamePaths.Count <= 1)
            {
                StatusText.Text = "No settings have changed.";
                return;
            }
            CheckMemory(request.Settings, request.Genome.Palette, request.Smoothing, change);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Check settings: {exception.Message}";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        RenderButton.Content = "Cancel";
        LoadButton.IsEnabled = false;
        SettingsPanel.IsEnabled = false;
        SaveButton.IsEnabled = false;
        RenderModeText.Text = change == FlameRenderChange.Sampling
            ? "Sampling flame points; press Cancel to stop."
            : "Applying image adjustments; press Cancel to stop.";
        StatusText.Text = change == FlameRenderChange.Sampling ? "Starting flame sampling…" : "Applying image adjustments…";
        try
        {
            if (_flamePaths.Count > 1)
            {
                await RenderBatchAsync(request, cancellation);
                return;
            }
            var progress = new ThrottledProgress(update => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(_cancellation, cancellation))
                    StatusText.Text = update.CompletedSamples >= update.TotalSamples
                        ? "Sampling complete; applying image adjustments…"
                        : $"Rendering on {request.Settings.WorkerCount} CPU workers… {GroupedIntegerInput.Format(update.CompletedSamples)}/{GroupedIntegerInput.Format(update.TotalSamples)} points";
            }));
            var prepared = await _session.PrepareAsync(request.Genome, request.Settings, request.Smoothing, progress, cancellation.Token, request.Curve, request.AutomaticCurve);
            var frame = prepared.Frame;
            StatusText.Text = "Preparing preview…";
            var bitmap = await Task.Run(frame.ToBitmapSource, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            PreviewImage.Source = bitmap;
            _session.Commit(prepared);
            _frame = frame;
            if (request.AutomaticCurve) CurveEditor.DisplayCurve(prepared.Curve);
            CurveEditor.Histogram = prepared.ColorHistogram;
            CurveEditor.OutputHistogram = frame.ColorHistogram;
            UpdateHistogramSummary();
            EmptyPreviewText.Visibility = Visibility.Collapsed;
            if (_fitToViewport) ApplyFitZoom(); else ApplyZoom(_zoomScale);
            StatusText.Text = change == FlameRenderChange.Sampling
                ? $"Rendered {GroupedIntegerInput.Format(request.Settings.Width)}×{GroupedIntegerInput.Format(request.Settings.Height)}. Save the PNG when ready."
                : "Image adjustments applied. Save the PNG when ready.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { StatusText.Text = "Render cancelled."; }
        catch (Exception exception) { StatusText.Text = $"Render failed: {exception.Message}"; }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose();
            RenderButton.Content = "Re-render";
            LoadButton.IsEnabled = true;
            SettingsPanel.IsEnabled = true;
            RenderButton.IsEnabled = _genome is not null;
            SaveButton.IsEnabled = _frame is not null;
            UpdateRenderMode();
        }
    }

    private async Task RenderBatchAsync(RenderRequest request, CancellationTokenSource cancellation)
    {
        var paths = _flamePaths.ToArray();
        var displayedIndex = 0;
        var progress = new Progress<FlameBatchProgress>(update =>
        {
            if (!ReferenceEquals(_cancellation, cancellation) || update.Index < displayedIndex) return;
            displayedIndex = update.Index;
            var samples = update.Sampling is { } value
                ? $" · {GroupedIntegerInput.Format(value.CompletedSamples)}/{GroupedIntegerInput.Format(value.TotalSamples)} points"
                : string.Empty;
            StatusText.Text = $"Batch {update.Index}/{update.Total}: {Path.GetFileName(update.SourcePath)}{samples}";
        });
        var batch = new FlameBatchRerenderer(_session);
        var results = await batch.RenderAsync(paths, request.Genome, request.Settings, request.Smoothing, request.Curve,
            request.AutomaticCurve, request.UseSourcePalette,
            (settings, palette, smoothing) => CheckMemory(settings, palette, smoothing, FlameRenderChange.Sampling), progress,
            async (path, genome, prepared, output) =>
            {
                var bitmap = await Task.Run(prepared.Frame.ToBitmapSource, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _updatingQueueSelection = true;
                try { FlameQueueList.SelectedItem = path; }
                finally { _updatingQueueSelection = false; }
                _genome = genome; FlamePathTextBox.Text = path; _frame = prepared.Frame;
                PreviewImage.Source = bitmap;
                if (request.AutomaticCurve) CurveEditor.DisplayCurve(prepared.Curve);
                CurveEditor.Histogram = prepared.ColorHistogram;
                CurveEditor.OutputHistogram = prepared.Frame.ColorHistogram;
                UpdateHistogramSummary(); EmptyPreviewText.Visibility = Visibility.Collapsed;
                if (_fitToViewport) ApplyFitZoom(); else ApplyZoom(_zoomScale);
                StatusText.Text = $"Saved {output}";
            }, cancellation.Token);
        var saved = results.Count(result => result.OutputPath is not null);
        var errors = results.Where(result => result.Error is not null).ToArray();
        StatusText.Text = errors.Length == 0
            ? $"Batch complete: {saved} images saved in rerendered subfolders."
            : $"Batch finished: {saved} saved; {errors.Length} failed. {Path.GetFileName(errors[0].SourcePath)}: {errors[0].Error}";
    }

    private RenderRequest ReadRequest()
    {
        var source = _genome ?? throw new InvalidOperationException("Load a flame first.");
        var size = (int)GroupedIntegerInput.Parse(SizeTextBox.Text, "Output size", 32, 10_000);
        var workers = ReadInt(WorkersTextBox, "CPU workers", 1, Math.Max(1, Environment.ProcessorCount));
        var samples = GroupedIntegerInput.Parse(SamplesTextBox.Text, "Sample budget", 100, RenderSettings.MaximumSampleBudget);
        var oversample = OversampleComboBox.SelectedIndex + 1;
        var smoothing = ReadDouble(SmoothingTextBox, "Smoothing", 0, 3);
        var palette = PaletteComboBox.SelectedIndex == 0 ? source.Palette : PaletteDefinition.BuiltIns[PaletteComboBox.SelectedIndex - 1];
        if (palette.Colors.SequenceEqual(PaletteDefinition.Monochrome.Colors)) palette = PaletteDefinition.Monochrome;
        var settings = new RenderSettings
        {
            Width = size, Height = size, SampleBudget = samples, Oversample = oversample, WorkerCount = workers,
            FilterRadius = ReadDouble(FilterTextBox, "Filter radius", 0, 3),
            Gamma = 1,
            Brightness = 1,
            Vibrancy = ReadDouble(VibrancyTextBox, "Vibrancy", 0, 1),
            WhitePoint = 0, BlackPoint = 1,
            ContrastCurve = 1,
            LowDensityCutoff = ReadDouble(CutoffTextBox, "Low-density cutoff", 0, 1),
            NormalizeDensityBySampleCount = StableToneCheckBox.IsChecked == true,
            PaletteName = palette.Name
        };
        var genome = source.Clone();
        new FlameCamera(
            ReadDouble(CenterXTextBox, "Center X", -1_000_000, 1_000_000),
            ReadDouble(CenterYTextBox, "Center Y", -1_000_000, 1_000_000),
            ReadDouble(ScaleTextBox, "Scale", 20, 400),
            ReadDouble(RotationTextBox, "Rotation", -1_000_000, 1_000_000)).ApplyTo(genome);
        genome.Palette = palette;
        genome.Width = size;
        genome.Height = size;
        genome.Quality = samples;
        genome.Oversample = oversample;
        genome.FilterRadius = settings.FilterRadius;
        genome.Gamma = settings.Gamma;
        genome.Brightness = settings.Brightness;
        genome.Vibrancy = settings.Vibrancy;
        return new RenderRequest(genome, settings, smoothing, CurveEditor.Curve, AutomaticCurveCheckBox.IsChecked == true, PaletteComboBox.SelectedIndex == 0);
    }

    private void UpdateRenderMode()
    {
        if (_cancellation is not null) return;
        if (_genome is null) { FitInputRangeButton.IsEnabled = false; RenderModeText.Text = "Load a flame to begin."; return; }
        try
        {
            var request = ReadRequest();
            var input = CurveEditor.Histogram;
            FitInputRangeButton.IsEnabled = !request.AutomaticCurve && _session.IsInputCurrent(request.Genome, request.Settings, request.Smoothing) &&
                input?.Minimum is { } min && input.Maximum is { } max && min < max;
            var change = _session.GetChange(request.Genome, request.Settings, request.Smoothing, request.Curve, request.AutomaticCurve);
            var message = _flamePaths.Count > 1 ? $"Next: render {_flamePaths.Count} queued flames in order and save each PNG." : change switch
            {
                FlameRenderChange.Sampling => "Next: sample flame points again.",
                FlameRenderChange.Adjustment => "Next: apply image adjustments; no new points.",
                _ => "No changes pending."
            };
            RenderModeText.Text = _frame is not null && change != FlameRenderChange.None
                ? message + " Preview shows the previous image."
                : message;
        }
        catch (InvalidOperationException) { FitInputRangeButton.IsEnabled = false; RenderModeText.Text = "Check settings before re-rendering."; }
    }

    private sealed record RenderRequest(FlameGenome Genome, RenderSettings Settings, double Smoothing, ToneCurve Curve, bool AutomaticCurve, bool UseSourcePalette);

    private static void CheckMemory(RenderSettings settings, PaletteDefinition palette, double smoothing, FlameRenderChange change)
    {
        var side = (long)settings.Width * settings.Oversample;
        var pixels = side * side;
        if (pixels > int.MaxValue / 4) throw new InvalidOperationException("This size and oversampling combination exceeds the renderer's image limit.");
        var monochrome = string.Equals(palette.Name, PaletteDefinition.Monochrome.Name, StringComparison.OrdinalIgnoreCase);
        var channels = monochrome ? 1L : 3L;
        var bytesPerInternalPixel = channels * 4 + (change == FlameRenderChange.Sampling ? (monochrome ? 8L : 32L) : 0L);
        var estimated = pixels * bytesPerInternalPixel + (long)settings.Width * settings.Height * channels * (smoothing > 0 ? 20L : 12L);
        var limit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 3 / 5;
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory)) limit = Math.Min(limit, (long)(memory.AvailablePhysical * 3 / 4));
        if (estimated > limit)
            throw new InvalidOperationException($"This combination needs about {estimated / 1_073_741_824d:F1} GB. Reduce size or oversampling to fit the memory limit.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static int ReadInt(WpfTextBox box, string name, int min, int max) =>
        int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value : throw new InvalidOperationException($"{name} must be an integer from {min:N0} to {max:N0}.");
    private static double ReadDouble(WpfTextBox box, string name, double min, double max) =>
        double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) && value >= min && value <= max
            ? value : throw new InvalidOperationException($"{name} must be between {min} and {max}; use a decimal point.");

    private void ResetCurve_Click(object sender, RoutedEventArgs e) => CurveEditor.Reset();

    private void AutomaticCurveChanged()
    {
        var automatic = AutomaticCurveCheckBox.IsChecked == true;
        CurveEditor.IsEnabled = !automatic;
        ResetCurveButton.IsEnabled = !automatic;
        UpdateRenderMode();
    }

    private void FitInputRange_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = CurveEditor.FitInputRange()
            ? "Input range fitted and curve reset. Press Re-render to apply."
            : "Render an image with more than one input value before fitting the range.";
    }

    private void UpdateHistogramSummary()
    {
        static string Percent(double value) => (value * 100).ToString("0.0000", CultureInfo.InvariantCulture) + "%";
        var points = CurveEditor.Curve.Points;
        MarkerRangeText.Text = $"Input markers: {Percent(points[0].X)} black · {Percent(points[^1].X)} white";
        var input = CurveEditor.Histogram;
        var output = CurveEditor.OutputHistogram;
        if (input?.Minimum is not { } min || input.Maximum is not { } max || output?.Minimum is not { } outputMin || output.Maximum is not { } outputMax)
        {
            ImageRangeText.Text = "Render an image to see its input and output channel ranges.";
            return;
        }
        ImageRangeText.Text = $"Input channels: {Percent(min)}–{Percent(max)}\nLast output: {(int)Math.Round(outputMin * 65535)}–{(int)Math.Round(outputMax * 65535)} / 65535";
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e) { _fitToViewport = true; ApplyFitZoom(); }
    private void ActualSize_Click(object sender, RoutedEventArgs e) { _fitToViewport = false; ApplyZoom(1); }

    private void PreviewScroll_MouseWheel(object sender, WpfMouseWheelEventArgs e)
    {
        if (_frame is null) return;
        _fitToViewport = false;
        ApplyZoom(_zoomScale * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(PreviewScroll));
        e.Handled = true;
    }

    private void PreviewScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitToViewport && _frame is not null) ApplyFitZoom();
    }

    private void ApplyFitZoom()
    {
        if (_frame is null) return;
        ApplyZoom(ViewportZoom.Fit(PreviewScroll.ViewportWidth, PreviewScroll.ViewportHeight, _frame.Width, _frame.Height));
    }

    private void ApplyZoom(double requestedScale, WpfPoint? anchor = null)
    {
        if (_applyingZoom || _frame is null) return;
        _applyingZoom = true;
        try
        {
            var oldScale = Math.Max(ViewportZoom.Minimum, _zoomScale);
            var anchorPoint = anchor ?? new WpfPoint(PreviewScroll.ViewportWidth / 2, PreviewScroll.ViewportHeight / 2);
            var origin = PreviewHost.TranslatePoint(new WpfPoint(0, 0), PreviewScroll);
            var imageX = Math.Clamp((anchorPoint.X - origin.X) / oldScale, 0, _frame.Width);
            var imageY = Math.Clamp((anchorPoint.Y - origin.Y) / oldScale, 0, _frame.Height);
            _zoomScale = ViewportZoom.Clamp(requestedScale);
            PreviewImage.Width = _frame.Width * _zoomScale;
            PreviewImage.Height = _frame.Height * _zoomScale;
            PreviewScroll.UpdateLayout();
            if (anchor is not null)
            {
                var newOrigin = PreviewHost.TranslatePoint(new WpfPoint(0, 0), PreviewScroll);
                PreviewScroll.ScrollToHorizontalOffset(PreviewScroll.HorizontalOffset + newOrigin.X - (anchorPoint.X - imageX * _zoomScale));
                PreviewScroll.ScrollToVerticalOffset(PreviewScroll.VerticalOffset + newOrigin.Y - (anchorPoint.Y - imageY * _zoomScale));
            }
        }
        finally { _applyingZoom = false; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_frame is null || string.IsNullOrWhiteSpace(FlamePathTextBox.Text)) return;
        var dialog = new SaveFileDialog
        {
            Filter = "16-bit PNG image (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(FlamePathTextBox.Text) + "_rerender.png",
            InitialDirectory = Path.GetDirectoryName(FlamePathTextBox.Text)
        };
        if (dialog.ShowDialog(this) != true) return;
        try { _frame.SavePng(dialog.FileName); StatusText.Text = $"Saved {dialog.FileName}"; }
        catch (Exception exception) { StatusText.Text = $"Could not save PNG: {exception.Message}"; }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellation?.Cancel();
        _session.Clear();
        base.OnClosed(e);
    }

    private sealed class ThrottledProgress(Action<RenderProgress> report) : IProgress<RenderProgress>
    {
        private long _last;
        public void Report(RenderProgress value)
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _last);
            if (value.CompletedSamples < value.TotalSamples && now - last < 100) return;
            if (Interlocked.CompareExchange(ref _last, now, last) == last) report(value);
        }
    }
}
