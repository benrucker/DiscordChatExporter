using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;

namespace DiscordChatExporter.Core.Utils;

/// <summary>
/// Tracks API call statistics including call counts, request times, and rate limit waits.
/// </summary>
public class ApiCallStatistics
{
    private readonly ConcurrentDictionary<string, EndpointStats> _statsByEndpoint = new();

    public void RecordCall(string endpoint, TimeSpan requestTime, TimeSpan rateLimitWait)
    {
        _statsByEndpoint.AddOrUpdate(
            endpoint,
            _ => new EndpointStats(1, requestTime, rateLimitWait),
            (_, existing) => existing.Add(requestTime, rateLimitWait)
        );
    }

    public bool HasCalls => !_statsByEndpoint.IsEmpty;

    public string GetSummary()
    {
        if (_statsByEndpoint.IsEmpty)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("API Statistics:");
        sb.AppendLine("    Endpoint             Calls    Request Time   Rate Limit Wait");
        sb.AppendLine("    ───────────────────────────────────────────────────────────────");

        var totalCalls = 0;
        var totalRequestTime = TimeSpan.Zero;
        var totalRateLimitWait = TimeSpan.Zero;

        // Sort by total time spent (request + wait) descending
        var sortedStats = _statsByEndpoint
            .OrderByDescending(kvp => kvp.Value.TotalRequestTime + kvp.Value.TotalRateLimitWait)
            .ToList();

        foreach (var (endpoint, stats) in sortedStats)
        {
            sb.AppendLine(
                $"    {endpoint, -20} {stats.CallCount, 5}     {FormatTime(stats.TotalRequestTime), 10}      {FormatTime(stats.TotalRateLimitWait), 10}"
            );

            totalCalls += stats.CallCount;
            totalRequestTime += stats.TotalRequestTime;
            totalRateLimitWait += stats.TotalRateLimitWait;
        }

        sb.AppendLine("    ───────────────────────────────────────────────────────────────");
        sb.AppendLine(
            $"    {"Total", -20} {totalCalls, 5}     {FormatTime(totalRequestTime), 10}      {FormatTime(totalRateLimitWait), 10}"
        );

        return sb.ToString();
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return $"{time.TotalHours:F1}h";
        if (time.TotalMinutes >= 1)
            return $"{time.TotalMinutes:F1}m";
        if (time.TotalSeconds >= 0.1)
            return $"{time.TotalSeconds:F1}s";
        return "0.0s";
    }

    private record EndpointStats(
        int CallCount,
        TimeSpan TotalRequestTime,
        TimeSpan TotalRateLimitWait
    )
    {
        public EndpointStats Add(TimeSpan requestTime, TimeSpan rateLimitWait) =>
            new(CallCount + 1, TotalRequestTime + requestTime, TotalRateLimitWait + rateLimitWait);
    }
}

/// <summary>
/// Provides an async-local statistics tracker that can be scoped to specific operations.
/// </summary>
public static class ScopedApiCallStatistics
{
    private static readonly AsyncLocal<ApiCallStatistics?> _current = new();

    /// <summary>
    /// Gets the current scoped statistics tracker, or null if none is set.
    /// </summary>
    public static ApiCallStatistics? Current => _current.Value;

    /// <summary>
    /// Sets the statistics tracker for the current async scope. Returns a disposable that restores the previous tracker.
    /// </summary>
    public static IDisposable Begin(ApiCallStatistics statistics)
    {
        var previous = _current.Value;
        _current.Value = statistics;
        return new Scope(previous);
    }

    private class Scope(ApiCallStatistics? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
