using System;
using System.Collections.Concurrent;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace DiscordChatExporter.Cli.Utils;

/// <summary>
/// A progress column that displays a status message stored per task.
/// </summary>
public class StatusColumn : ProgressColumn
{
    // Store status per task using task's Id
    private static readonly ConcurrentDictionary<int, string> StatusByTaskId = new();

    public static void SetStatus(ProgressTask task, string status)
    {
        StatusByTaskId[task.Id] = status;
    }

    public static void ClearStatus(ProgressTask task)
    {
        StatusByTaskId.TryRemove(task.Id, out _);
    }

    public override IRenderable Render(RenderOptions options, ProgressTask task, TimeSpan deltaTime)
    {
        if (!StatusByTaskId.TryGetValue(task.Id, out var status) || string.IsNullOrEmpty(status))
            return Text.Empty;

        return new Markup($"[grey]{Markup.Escape(status)}[/]");
    }
}
