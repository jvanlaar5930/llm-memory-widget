using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LlmMemoryWidget;

public sealed class GpuProcessSnapshot
{
    public long DedicatedBytes { get; init; }

    public double UtilizationPercent { get; init; }

    public bool IsMemoryAvailable { get; init; }

    public bool IsUtilizationAvailable { get; init; }

    public string? Error { get; init; }

    public double DedicatedGb => DedicatedBytes / 1024d / 1024d / 1024d;
}

public static class GpuProcessReader
{
    private const string GpuProcessMemoryCategoryName = "GPU Process Memory";
    private const string DedicatedCounterName = "Dedicated Usage";

    private const string GpuEngineCategoryName = "GPU Engine";
    private const string UtilizationCounterName = "Utilization Percentage";

    public static GpuProcessSnapshot Read(IEnumerable<int> processIds)
    {
        var pidSet = processIds.ToHashSet();

        if (pidSet.Count == 0)
        {
            return new GpuProcessSnapshot
            {
                IsMemoryAvailable = true,
                IsUtilizationAvailable = true,
                DedicatedBytes = 0,
                UtilizationPercent = 0
            };
        }

        var dedicatedResult = ReadDedicatedUsageBytes(pidSet);
        var utilizationResult = ReadUtilizationPercent(pidSet);

        return new GpuProcessSnapshot
        {
            DedicatedBytes = dedicatedResult.Value,
            UtilizationPercent = utilizationResult.Value,
            IsMemoryAvailable = dedicatedResult.IsAvailable,
            IsUtilizationAvailable = utilizationResult.IsAvailable,
            Error = dedicatedResult.Error ?? utilizationResult.Error
        };
    }

    private static CounterReadResult<long> ReadDedicatedUsageBytes(HashSet<int> pidSet)
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(GpuProcessMemoryCategoryName))
                return CounterReadResult<long>.Unavailable("GPU Process Memory counter not found");

            var category = new PerformanceCounterCategory(GpuProcessMemoryCategoryName);

            long totalBytes = 0;

            foreach (var instanceName in category.GetInstanceNames())
            {
                var pid = TryReadPidFromInstanceName(instanceName);

                if (pid is null || !pidSet.Contains(pid.Value))
                    continue;

                using var counter = new PerformanceCounter(
                    GpuProcessMemoryCategoryName,
                    DedicatedCounterName,
                    instanceName,
                    readOnly: true);

                totalBytes += Math.Max(0, counter.RawValue);
            }

            return CounterReadResult<long>.Available(totalBytes);
        }
        catch (Exception ex)
        {
            return CounterReadResult<long>.Unavailable(ex.Message);
        }
    }

    private static CounterReadResult<double> ReadUtilizationPercent(HashSet<int> pidSet)
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(GpuEngineCategoryName))
                return CounterReadResult<double>.Unavailable("GPU Engine counter not found");

            var category = new PerformanceCounterCategory(GpuEngineCategoryName);

            double totalPercent = 0;

            foreach (var instanceName in category.GetInstanceNames())
            {
                var pid = TryReadPidFromInstanceName(instanceName);

                if (pid is null || !pidSet.Contains(pid.Value))
                    continue;

                using var counter = new PerformanceCounter(
                    GpuEngineCategoryName,
                    UtilizationCounterName,
                    instanceName,
                    readOnly: true);

                totalPercent += Math.Max(0, counter.NextValue());
            }

            return CounterReadResult<double>.Available(Math.Clamp(totalPercent, 0, 100));
        }
        catch (Exception ex)
        {
            return CounterReadResult<double>.Unavailable(ex.Message);
        }
    }

    private static int? TryReadPidFromInstanceName(string instanceName)
    {
        var match = Regex.Match(instanceName, @"(?:^|_)pid_(\d+)(?:_|$)", RegexOptions.IgnoreCase);

        if (!match.Success)
            return null;

        return int.TryParse(match.Groups[1].Value, out var pid)
            ? pid
            : null;
    }

    private sealed class CounterReadResult<T>
    {
        public T Value { get; init; } = default!;

        public bool IsAvailable { get; init; }

        public string? Error { get; init; }

        public static CounterReadResult<T> Available(T value)
        {
            return new CounterReadResult<T>
            {
                IsAvailable = true,
                Value = value
            };
        }

        public static CounterReadResult<T> Unavailable(string error)
        {
            return new CounterReadResult<T>
            {
                IsAvailable = false,
                Error = error
            };
        }
    }
}
