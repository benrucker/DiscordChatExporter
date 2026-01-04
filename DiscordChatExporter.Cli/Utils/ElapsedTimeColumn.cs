using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DiscordChatExporter.Cli.Utils;

/// <summary>
/// A progress column that displays elapsed time per task using a stopwatch.
/// </summary>
public class ElapsedTimeColumn : ProgressColumn
{
    private static readonly ConcurrentDictionary<int, Stopwatch> StopwatchByTaskId = new();
    private static readonly ConcurrentDictionary<int, TimeSpan> FinalDurationByTaskId = new();

    /// <summary>
    /// Starts timing for a task.
    /// </summary>
    public static void StartTiming(ProgressTask task)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        StopwatchByTaskId[task.Id] = stopwatch;
    }

    /// <summary>
    /// Stops timing for a task and returns the elapsed time.
    /// The final time will continue to be displayed in a muted color.
    /// </summary>
    public static TimeSpan StopTiming(ProgressTask task)
    {
        if (StopwatchByTaskId.TryRemove(task.Id, out var stopwatch))
        {
            stopwatch.Stop();
            var elapsed = stopwatch.Elapsed;
            FinalDurationByTaskId[task.Id] = elapsed;
            return elapsed;
        }
        return TimeSpan.Zero;
    }

    /// <summary>
    /// Gets the current elapsed time for a task without stopping it.
    /// </summary>
    public static TimeSpan GetElapsed(ProgressTask task)
    {
        if (StopwatchByTaskId.TryGetValue(task.Id, out var stopwatch))
        {
            return stopwatch.Elapsed;
        }
        return TimeSpan.Zero;
    }

    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        // Check if task is still running
        if (StopwatchByTaskId.TryGetValue(task.Id, out var stopwatch))
        {
            var elapsed = stopwatch.Elapsed;
            var formattedTime = FormatElapsed(elapsed);
            // Use dim grey for running time (subtle, in the background)
            return new Markup($"[grey]{formattedTime}[/]");
        }

        // Check if task has completed
        if (FinalDurationByTaskId.TryGetValue(task.Id, out var finalDuration))
        {
            var formattedTime = FormatElapsed(finalDuration);
            // Use dim grey for final time (muted)
            return new Markup($"[grey]{formattedTime}[/]");
        }

        return Text.Empty;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
            return $"{(int)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        if (elapsed.TotalMinutes >= 1)
            return $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}";
        return $"{elapsed.Seconds}.{elapsed.Milliseconds / 100}s";
    }
}
