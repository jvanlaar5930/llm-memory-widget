using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LlmMemoryWidget;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    private WidgetSettings _settings = new();
    private DateTime _lastCpuSampleTime = DateTime.UtcNow;
    private TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
    private FocusedColumn _focusedColumn = FocusedColumn.None;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => ApplyModeDefaults();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };

        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        try
        {
            _settings = ReadSettings();

            var processMemory = GetProcessMemory(_settings.ProcessName);
            var gpu = GpuProcessReader.Read(processMemory.ProcessIds);
            var cpuPercent = ReadProcessCpuPercent(processMemory.TotalProcessorTime);
            var systemMemory = SystemMemoryReader.Read();

            var endpointBody = await TryReadEndpointAsync(_settings.Endpoint);
            var runtimeMetrics = LlmMetricsParser.Parse(endpointBody);

            var snapshot = BuildSnapshot(_settings, processMemory, gpu, cpuPercent, systemMemory, runtimeMetrics);

            ApplySnapshot(snapshot);

            StatusText.Text = $"{_settings.Mode} | refresh 1s | {DateTime.Now:T}";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private WidgetSettings ReadSettings()
    {
        var modeText = ((ComboBoxItem)ModeCombo.SelectedItem).Content.ToString();

        var mode = modeText?.Contains("llama", StringComparison.OrdinalIgnoreCase) == true
            ? BackendMode.LlamaCpp
            : BackendMode.LmStudio;

        var totalGb = TryParseGb(TotalGbBox.Text, 12);

        return new WidgetSettings
        {
            Mode = mode,
            ProcessName = ProcessNameBox.Text.Trim(),
            Endpoint = EndpointBox.Text.Trim(),
            TotalVramGb = totalGb
        };
    }

    private static DisplaySnapshot BuildSnapshot(
        WidgetSettings settings,
        ProcessMemorySnapshot processMemory,
        GpuProcessSnapshot gpu,
        double cpuPercent,
        SystemMemorySnapshot systemMemory,
        LlmRuntimeMetrics runtimeMetrics)
    {
        var dedicatedGpuGb = gpu.IsMemoryAvailable
            ? Math.Clamp(gpu.DedicatedGb, 0, settings.TotalVramGb)
            : 0;

        var modelGb = runtimeMetrics.ModelVramGb;
        var kvGb = runtimeMetrics.KvCacheVramGb;
        var computeGb = runtimeMetrics.ComputeVramGb;
        var otherGb = runtimeMetrics.OtherVramGb;

        /*
         * If llama.cpp / LM Studio exposes a memory breakdown, prefer it.
         * If not, split dedicated GPU memory into useful LLM-oriented estimates.
         */
        if (!runtimeMetrics.HasAnyVramBreakdown)
        {
            if (runtimeMetrics.KvCacheUsedRatio is { } kvRatio)
            {
                kvGb = dedicatedGpuGb * 0.25 * kvRatio;
                computeGb = dedicatedGpuGb * 0.10;
                modelGb = Math.Max(0, dedicatedGpuGb - kvGb.Value - computeGb.Value);
                otherGb = 0;
            }
            else
            {
                modelGb = dedicatedGpuGb * 0.72;
                kvGb = dedicatedGpuGb * 0.18;
                computeGb = dedicatedGpuGb * 0.06;
                otherGb = dedicatedGpuGb * 0.04;
            }
        }
        else
        {
            modelGb ??= 0;
            kvGb ??= 0;
            computeGb ??= 0;
            var known = modelGb.Value + kvGb.Value + computeGb.Value + (otherGb ?? 0);

            /*
             * When endpoint metrics give only model or KV but not everything,
             * assign the remaining dedicated GPU memory into Other.
             */
            otherGb ??= Math.Max(0, dedicatedGpuGb - known);
        }

        var usedGpuGb = modelGb.GetValueOrDefault() +
                        kvGb.GetValueOrDefault() +
                        computeGb.GetValueOrDefault() +
                        otherGb.GetValueOrDefault();

        if (usedGpuGb > settings.TotalVramGb)
        {
            var scale = settings.TotalVramGb / usedGpuGb;
            modelGb *= scale;
            kvGb *= scale;
            computeGb *= scale;
            otherGb *= scale;
            usedGpuGb = settings.TotalVramGb;
        }

        var gpuFreeGb = Math.Max(0, settings.TotalVramGb - usedGpuGb);

        var processRamGb = runtimeMetrics.RamGb ??
                           Math.Max(0, processMemory.WorkingSetGb - dedicatedGpuGb);

        var systemRamFreeGb = Math.Max(0, systemMemory.TotalPhysicalGb - processRamGb);

        var gpuUtilization = gpu.IsUtilizationAvailable
            ? gpu.UtilizationPercent
            : 0;

        var idlePercent = Math.Max(0, 100 - Math.Max(gpuUtilization, cpuPercent));

        var metricsSource = runtimeMetrics.HasAny
            ? runtimeMetrics.Source
            : "process counters + estimates";

        return new DisplaySnapshot
        {
            TotalVramGb = settings.TotalVramGb,
            GpuModelGb = modelGb.GetValueOrDefault(),
            GpuKvCacheGb = kvGb.GetValueOrDefault(),
            GpuComputeGb = computeGb.GetValueOrDefault(),
            GpuOtherGb = otherGb.GetValueOrDefault(),
            GpuFreeGb = gpuFreeGb,

            TotalSystemRamGb = systemMemory.TotalPhysicalGb,
            RemainingProcessRamGb = processRamGb,
            SystemRamFreeGb = systemRamFreeGb,

            GpuUtilizationPercent = gpuUtilization,
            CpuUtilizationPercent = cpuPercent,
            ComputeIdlePercent = idlePercent,

            GpuMemoryAvailable = gpu.IsMemoryAvailable,
            GpuUtilizationAvailable = gpu.IsUtilizationAvailable,
            MetricsSource = metricsSource
        };
    }

    private static ProcessMemorySnapshot GetProcessMemory(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return ProcessMemorySnapshot.Empty;

        var normalized = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4]
            : processName;

        var processes = Process.GetProcessesByName(normalized);

        if (processes.Length == 0)
            return ProcessMemorySnapshot.Empty;

        long bytes = 0;
        var totalProcessorTime = TimeSpan.Zero;
        var pids = new List<int>();

        foreach (var process in processes)
        {
            try
            {
                pids.Add(process.Id);
                bytes += process.WorkingSet64;
                totalProcessorTime += process.TotalProcessorTime;
            }
            catch
            {
                // Ignore processes that exit while being read.
            }
        }

        return new ProcessMemorySnapshot
        {
            ProcessIds = pids,
            WorkingSetBytes = bytes,
            TotalProcessorTime = totalProcessorTime
        };
    }

    private double ReadProcessCpuPercent(TimeSpan currentTotalProcessorTime)
    {
        var now = DateTime.UtcNow;
        var elapsedMs = (now - _lastCpuSampleTime).TotalMilliseconds;
        var cpuMs = (currentTotalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds;

        _lastCpuSampleTime = now;
        _lastTotalProcessorTime = currentTotalProcessorTime;

        if (elapsedMs <= 0)
            return 0;

        var cpuPercent = cpuMs / elapsedMs / Environment.ProcessorCount * 100d;

        return Math.Clamp(cpuPercent, 0, 100);
    }

    private async Task<string?> TryReadEndpointAsync(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;

        try
        {
            return await _http.GetStringAsync(endpoint);
        }
        catch
        {
            return null;
        }
    }

    private static double TryParseGb(string text, double fallback)
    {
        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0.1, value)
            : fallback;
    }


    private static GridLength VisibleRow(double value)
    {
        /*
         * Keep each visual segment readable even when the underlying value is zero.
         * The text still shows the true value, e.g. 0.0 GB, but the row keeps
         * enough visual space for the label and value.
         */
        return new GridLength(Math.Max(0.25, value), GridUnitType.Star);
    }

    private void ApplySnapshot(DisplaySnapshot snapshot)
    {
        GpuFreeRow.Height = VisibleRow(snapshot.GpuFreeGb);
        GpuModelRow.Height = VisibleRow(snapshot.GpuModelGb);
        GpuKvRow.Height = VisibleRow(snapshot.GpuKvCacheGb);
        GpuComputeRow.Height = VisibleRow(snapshot.GpuComputeGb);
        GpuOtherRow.Height = VisibleRow(snapshot.GpuOtherGb);

        RamFreeRow.Height = VisibleRow(snapshot.SystemRamFreeGb);
        RamUsedRow.Height = VisibleRow(snapshot.RemainingProcessRamGb);

        ComputeIdleRow.Height = VisibleRow(snapshot.ComputeIdlePercent);
        GpuUtilRow.Height = VisibleRow(snapshot.GpuUtilizationPercent);
        CpuUtilRow.Height = VisibleRow(snapshot.CpuUtilizationPercent);

        GpuFreeText.Text = $"{snapshot.GpuFreeGb:0.0} GB";
        GpuModelText.Text = snapshot.GpuMemoryAvailable ? $"{snapshot.GpuModelGb:0.0} GB" : "unavailable";
        GpuKvText.Text = $"{snapshot.GpuKvCacheGb:0.0} GB";
        GpuComputeText.Text = $"{snapshot.GpuComputeGb:0.0} GB";
        GpuOtherText.Text = $"{snapshot.GpuOtherGb:0.0} GB";

        var usedGpu = snapshot.GpuModelGb + snapshot.GpuKvCacheGb + snapshot.GpuComputeGb + snapshot.GpuOtherGb;
        GpuTotalText.Text = $"{usedGpu:0.0} / {snapshot.TotalVramGb:0.0} GB";

        RamFreeText.Text = $"{snapshot.SystemRamFreeGb:0.0} GB";
        RamUsedText.Text = $"{snapshot.RemainingProcessRamGb:0.0} GB";
        RamTotalText.Text = $"Process RAM: {snapshot.RemainingProcessRamGb:0.0} GB";

        ComputeIdleText.Text = $"{snapshot.ComputeIdlePercent:0}%";
        GpuUtilText.Text = snapshot.GpuUtilizationAvailable
            ? $"{snapshot.GpuUtilizationPercent:0}%"
            : "n/a";
        CpuUtilText.Text = $"{snapshot.CpuUtilizationPercent:0}%";
        ComputeTotalText.Text = $"GPU {snapshot.GpuUtilizationPercent:0}% / CPU {snapshot.CpuUtilizationPercent:0}%";

        MetricsSourceText.Text = $"metrics: {snapshot.MetricsSource}";
    }


    private void ColumnDoubleClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
            return;

        var clickedColumn = sender switch
        {
            Border border when border == GpuColumnBorder => FocusedColumn.Gpu,
            Border border when border == RamColumnBorder => FocusedColumn.Ram,
            Border border when border == ComputeColumnBorder => FocusedColumn.Compute,
            _ => FocusedColumn.None
        };

        if (clickedColumn == FocusedColumn.None)
            return;

        _focusedColumn = _focusedColumn == clickedColumn
            ? FocusedColumn.None
            : clickedColumn;

        ApplyColumnFocus();
        e.Handled = true;
    }

    private void ApplyColumnFocus()
    {
        if (GpuColumnBorder is null ||
            RamColumnBorder is null ||
            ComputeColumnBorder is null ||
            GpuColumnDefinition is null ||
            RamColumnDefinition is null ||
            ComputeColumnDefinition is null ||
            LeftSpacerColumnDefinition is null ||
            RightSpacerColumnDefinition is null)
        {
            return;
        }

        if (_focusedColumn == FocusedColumn.None)
        {
            GpuColumnBorder.Visibility = Visibility.Visible;
            RamColumnBorder.Visibility = Visibility.Visible;
            ComputeColumnBorder.Visibility = Visibility.Visible;

            GpuColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            RamColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            ComputeColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            LeftSpacerColumnDefinition.Width = new GridLength(12);
            RightSpacerColumnDefinition.Width = new GridLength(12);
            return;
        }

        GpuColumnBorder.Visibility = _focusedColumn == FocusedColumn.Gpu
            ? Visibility.Visible
            : Visibility.Collapsed;

        RamColumnBorder.Visibility = _focusedColumn == FocusedColumn.Ram
            ? Visibility.Visible
            : Visibility.Collapsed;

        ComputeColumnBorder.Visibility = _focusedColumn == FocusedColumn.Compute
            ? Visibility.Visible
            : Visibility.Collapsed;

        GpuColumnDefinition.Width = _focusedColumn == FocusedColumn.Gpu
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        RamColumnDefinition.Width = _focusedColumn == FocusedColumn.Ram
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        ComputeColumnDefinition.Width = _focusedColumn == FocusedColumn.Compute
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);

        LeftSpacerColumnDefinition.Width = new GridLength(0);
        RightSpacerColumnDefinition.Width = new GridLength(0);
    }

    private void SettingsChanged(object sender, EventArgs e)
    {
        if (!IsLoaded)
            return;

        if (sender == ModeCombo)
            ApplyModeDefaults();
    }

    private void ApplyModeDefaults()
    {
        if (ModeCombo is null || ProcessNameBox is null || EndpointBox is null)
            return;

        if (ModeCombo.SelectedItem is not ComboBoxItem selected)
            return;

        var modeText = selected.Content?.ToString() ?? string.Empty;

        if (modeText.Contains("llama", StringComparison.OrdinalIgnoreCase))
        {
            ProcessNameBox.Text = "llama-server";
            EndpointBox.Text = "http://127.0.0.1:8080/metrics";
        }
        else
        {
            ProcessNameBox.Text = "LM Studio";
            EndpointBox.Text = string.Empty;
        }
    }
}


public enum FocusedColumn
{
    None,
    Gpu,
    Ram,
    Compute
}

public enum BackendMode
{
    LmStudio,
    LlamaCpp
}

public sealed class WidgetSettings
{
    public BackendMode Mode { get; set; } = BackendMode.LmStudio;

    public string ProcessName { get; set; } = "LM Studio";

    public string Endpoint { get; set; } = "http://127.0.0.1:8080/metrics";

    public double TotalVramGb { get; set; } = 12;
}

public sealed class ProcessMemorySnapshot
{
    public static ProcessMemorySnapshot Empty { get; } = new()
    {
        ProcessIds = [],
        WorkingSetBytes = 0,
        TotalProcessorTime = TimeSpan.Zero
    };

    public IReadOnlyList<int> ProcessIds { get; init; } = [];

    public long WorkingSetBytes { get; init; }

    public TimeSpan TotalProcessorTime { get; init; }

    public double WorkingSetGb => WorkingSetBytes / 1024d / 1024d / 1024d;
}

public sealed class DisplaySnapshot
{
    public double TotalVramGb { get; init; }

    public double GpuModelGb { get; init; }

    public double GpuKvCacheGb { get; init; }

    public double GpuComputeGb { get; init; }

    public double GpuOtherGb { get; init; }

    public double GpuFreeGb { get; init; }

    public double TotalSystemRamGb { get; init; }

    public double RemainingProcessRamGb { get; init; }

    public double SystemRamFreeGb { get; init; }

    public double GpuUtilizationPercent { get; init; }

    public double CpuUtilizationPercent { get; init; }

    public double ComputeIdlePercent { get; init; }

    public bool GpuMemoryAvailable { get; init; }

    public bool GpuUtilizationAvailable { get; init; }

    public string MetricsSource { get; init; } = "process counters + estimates";
}
