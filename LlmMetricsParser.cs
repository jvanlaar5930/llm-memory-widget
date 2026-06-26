using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmMemoryWidget;

public sealed class LlmRuntimeMetrics
{
    public double? ModelVramGb { get; set; }

    public double? KvCacheVramGb { get; set; }

    public double? ComputeVramGb { get; set; }

    public double? OtherVramGb { get; set; }

    public double? RamGb { get; set; }

    public double? KvCacheUsedRatio { get; set; }

    public string Source { get; set; } = "estimated";

    public bool HasAnyVramBreakdown =>
        ModelVramGb.HasValue ||
        KvCacheVramGb.HasValue ||
        ComputeVramGb.HasValue ||
        OtherVramGb.HasValue;

    public bool HasAny =>
        HasAnyVramBreakdown ||
        RamGb.HasValue ||
        KvCacheUsedRatio.HasValue;
}

public static class LlmMetricsParser
{
    public static LlmRuntimeMetrics Parse(string? body)
    {
        var result = new LlmRuntimeMetrics();

        if (string.IsNullOrWhiteSpace(body))
            return result;

        var trimmed = body.TrimStart();

        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            ParseJsonMetrics(trimmed, result);
        }
        else
        {
            ParsePrometheusMetrics(body, result);
        }

        return result;
    }

    private static void ParsePrometheusMetrics(string body, LlmRuntimeMetrics result)
    {
        var metrics = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith("#"))
                continue;

            var match = Regex.Match(line, @"^(?<name>[a-zA-Z_:][a-zA-Z0-9_:{}=,""\.\-]*)\s+(?<value>[-+]?\d+(\.\d+)?([eE][-+]?\d+)?)$");

            if (!match.Success)
                continue;

            var fullName = match.Groups["name"].Value;
            var metricName = fullName.Split('{')[0];

            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                continue;

            metrics[metricName] = value;
        }

        result.ModelVramGb = FindByteMetricGb(metrics,
            "model", "vram", "used",
            "model", "gpu", "memory",
            "model", "memory", "bytes");

        result.KvCacheVramGb = FindByteMetricGb(metrics,
            "kv", "cache", "bytes",
            "kv", "cache", "used",
            "kv", "vram",
            "cache", "vram");

        result.ComputeVramGb = FindByteMetricGb(metrics,
            "compute", "vram",
            "compute", "memory",
            "workspace", "bytes",
            "graph", "memory");

        result.OtherVramGb = FindByteMetricGb(metrics,
            "other", "vram",
            "scratch", "bytes",
            "buffer", "bytes");

        result.RamGb = FindByteMetricGb(metrics,
            "ram", "used",
            "cpu", "memory",
            "host", "memory",
            "resident", "memory");

        var kvUsedCells = FindMetric(metrics, "kv", "cache", "used", "cells")
                          ?? FindMetric(metrics, "kv_cache_used_cells")
                          ?? FindMetric(metrics, "llamacpp:kv_cache_used_cells");

        var kvTotalCells = FindMetric(metrics, "kv", "cache", "total", "cells")
                           ?? FindMetric(metrics, "kv_cache_total_cells")
                           ?? FindMetric(metrics, "llamacpp:kv_cache_total_cells");

        if (kvUsedCells is > 0 && kvTotalCells is > 0)
            result.KvCacheUsedRatio = Math.Clamp(kvUsedCells.Value / kvTotalCells.Value, 0, 1);

        if (result.HasAny)
            result.Source = "endpoint";
    }

    private static void ParseJsonMetrics(string body, LlmRuntimeMetrics result)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var flattened = new List<(string Path, double Value)>();
            FlattenJsonNumbers(doc.RootElement, "", flattened);

            result.ModelVramGb = FindJsonGb(flattened,
                "model", "vram",
                "model", "gpu",
                "model", "memory");

            result.KvCacheVramGb = FindJsonGb(flattened,
                "kv", "cache", "vram",
                "kv", "cache", "gpu",
                "kv", "cache", "memory");

            result.ComputeVramGb = FindJsonGb(flattened,
                "compute", "vram",
                "compute", "memory",
                "workspace", "memory");

            result.OtherVramGb = FindJsonGb(flattened,
                "other", "vram",
                "scratch", "memory",
                "buffer", "memory");

            result.RamGb = FindJsonGb(flattened,
                "ram", "used",
                "cpu", "memory",
                "host", "memory",
                "resident", "memory");

            var kvUsed = FindJsonValue(flattened, "kv", "cache", "used");
            var kvTotal = FindJsonValue(flattened, "kv", "cache", "total");

            if (kvUsed is > 0 && kvTotal is > 0)
                result.KvCacheUsedRatio = Math.Clamp(kvUsed.Value / kvTotal.Value, 0, 1);

            if (result.HasAny)
                result.Source = "endpoint-json";
        }
        catch
        {
            // Ignore bad JSON and fall back to process counters / estimates.
        }
    }

    private static void FlattenJsonNumbers(JsonElement element, string path, List<(string Path, double Value)> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    FlattenJsonNumbers(property.Value, Append(path, property.Name), values);
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var child in element.EnumerateArray())
                    FlattenJsonNumbers(child, Append(path, index++.ToString(CultureInfo.InvariantCulture)), values);
                break;

            case JsonValueKind.Number:
                if (element.TryGetDouble(out var value))
                    values.Add((path, value));
                break;
        }
    }

    private static string Append(string path, string name)
    {
        return string.IsNullOrWhiteSpace(path)
            ? name
            : $"{path}.{name}";
    }

    private static double? FindByteMetricGb(Dictionary<string, double> metrics, params string[] groups)
    {
        var value = FindMetric(metrics, groups);

        if (value is null)
            return null;

        return BytesToGb(value.Value);
    }

    private static double? FindMetric(Dictionary<string, double> metrics, params string[] containsAll)
    {
        foreach (var pair in metrics)
        {
            if (ContainsAll(pair.Key, containsAll))
                return pair.Value;
        }

        return null;
    }

    private static double? FindJsonGb(List<(string Path, double Value)> values, params string[] groups)
    {
        var item = values.FirstOrDefault(v => ContainsAll(v.Path, groups));

        if (item.Path is null)
            return null;

        return LooksLikeBytes(item.Path, item.Value)
            ? BytesToGb(item.Value)
            : item.Value;
    }

    private static double? FindJsonValue(List<(string Path, double Value)> values, params string[] containsAll)
    {
        var item = values.FirstOrDefault(v => ContainsAll(v.Path, containsAll));

        return item.Path is null
            ? null
            : item.Value;
    }

    private static bool ContainsAll(string text, params string[] terms)
    {
        if (terms.Length == 0)
            return false;

        var lower = text.ToLowerInvariant();

        foreach (var term in terms)
        {
            if (!lower.Contains(term.ToLowerInvariant()))
                return false;
        }

        return true;
    }

    private static bool LooksLikeBytes(string name, double value)
    {
        var lower = name.ToLowerInvariant();

        return lower.Contains("bytes") ||
               lower.Contains("_b") ||
               lower.Contains("byte") ||
               value > 1024 * 1024;
    }

    private static double BytesToGb(double bytes)
    {
        return bytes / 1024d / 1024d / 1024d;
    }
}
