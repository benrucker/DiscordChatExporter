using DiscordChatExporter.Core.Utils;
using Spectre.Console;

namespace DiscordChatExporter.Cli.Utils;

/// <summary>
/// Status logger that outputs messages above the progress bar using Spectre.Console.
/// </summary>
public class SpectreStatusLogger : IStatusLogger
{
    public void Log(string message)
    {
        AnsiConsole.MarkupLine($"[grey]    {Markup.Escape(message)}[/]");
    }
}

/// <summary>
/// Status logger that updates a ProgressTask's status column.
/// </summary>
public class ProgressTaskStatusLogger(ProgressTask task) : IStatusLogger
{
    public void Log(string message)
    {
        StatusColumn.SetStatus(task, message);
    }
}
