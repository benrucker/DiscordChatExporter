using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CliFx.Attributes;
using CliFx.Exceptions;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Converters;
using DiscordChatExporter.Cli.Commands.Shared;
using DiscordChatExporter.Cli.Utils;
using DiscordChatExporter.Cli.Utils.Extensions;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using DiscordChatExporter.Core.Utils;
using Gress;
using Spectre.Console;
using MemberCache = DiscordChatExporter.Core.Exporting.MemberCache;

namespace DiscordChatExporter.Cli.Commands.Base;

public abstract class ExportCommandBase : DiscordCommandBase
{
    [CommandOption(
        "output",
        'o',
        Description = "Output file or directory path. "
            + "If a directory is specified, file names will be generated automatically based on the channel names and export parameters. "
            + "Directory paths must end with a slash to avoid ambiguity. "
            + "Supports template tokens, see the documentation for more info."
    )]
    public string OutputPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        init => field = Path.GetFullPath(value);
    } = Directory.GetCurrentDirectory();

    [CommandOption("format", 'f', Description = "Export format.")]
    public ExportFormat ExportFormat { get; init; } = ExportFormat.HtmlDark;

    [CommandOption(
        "after",
        Description = "Only include messages sent after this date or message ID."
    )]
    public Snowflake? After { get; init; }

    [CommandOption(
        "before",
        Description = "Only include messages sent before this date or message ID."
    )]
    public Snowflake? Before { get; init; }

    [CommandOption(
        "partition",
        'p',
        Description = "Split the output into partitions, each limited to the specified "
            + "number of messages (e.g. '100') or file size (e.g. '10mb')."
    )]
    public PartitionLimit PartitionLimit { get; init; } = PartitionLimit.Null;

    [CommandOption(
        "include-threads",
        Description = "Which types of threads should be included.",
        Converter = typeof(ThreadInclusionModeBindingConverter)
    )]
    public ThreadInclusionMode ThreadInclusionMode { get; init; } = ThreadInclusionMode.None;

    [CommandOption(
        "filter",
        Description = "Only include messages that satisfy this filter. "
            + "See the documentation for more info."
    )]
    public MessageFilter MessageFilter { get; init; } = MessageFilter.Null;

    [CommandOption(
        "first-day-of-month",
        Description = "When used with --after and --before, exports only the first day of each month "
            + "within the specified range. Each month produces a separate output file."
    )]
    public bool IsFirstDayOfMonthMode { get; init; } = false;

    [CommandOption(
        "parallel",
        Description = "Limits how many channels can be exported in parallel."
    )]
    public int ParallelLimit { get; init; } = 1;

    [CommandOption(
        "markdown",
        Description = "Process markdown, mentions, and other special tokens."
    )]
    public bool ShouldFormatMarkdown { get; init; } = true;

    [CommandOption(
        "media",
        Description = "Download assets referenced by the export (user avatars, attached files, embedded images, etc.)."
    )]
    public bool ShouldDownloadAssets { get; init; }

    [CommandOption(
        "skip-bot-attachments",
        Description = "Skip downloading file attachments from bot accounts. "
            + "Avatars, emojis, and other assets from bots are still downloaded."
    )]
    public bool ShouldSkipBotAttachments { get; init; } = false;

    [CommandOption(
        "reuse-media",
        Description = "Reuse previously downloaded assets to avoid redundant requests. "
            + "Keep --media-nested consistent across runs for best results."
    )]
    public bool ShouldReuseAssets { get; init; } = false;

    [CommandOption(
        "media-nested",
        Description = "Saves assets with a nested file naming convention, creating subdirectories for each distinct asset type."
    )]
    public bool ShouldUseNestedMediaFilePaths { get; init; } = false;

    [CommandOption(
        "media-dir",
        Description = "Download assets to this directory. "
            + "If not specified, the asset directory path will be derived from the output path."
    )]
    public string? AssetsDirPath
    {
        get;
        // Handle ~/ in paths on Unix systems
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/903
        init => field = value is not null ? Path.GetFullPath(value) : null;
    }

    [Obsolete("This option doesn't do anything. Kept for backwards compatibility.")]
    [CommandOption(
        "dateformat",
        Description = "This option doesn't do anything. Kept for backwards compatibility."
    )]
    public string DateFormat { get; init; } = "MM/dd/yyyy h:mm tt";

    [CommandOption(
        "locale",
        Description = "Locale to use when formatting dates and numbers. "
            + "If not specified, the default system locale will be used."
    )]
    public string? Locale { get; init; }

    [CommandOption("utc", Description = "Normalize all timestamps to UTC+0.")]
    public bool IsUtcNormalizationEnabled { get; init; } = false;

    [CommandOption(
        "normalize-json",
        Description = "Normalize JSON output by extracting users, members, and roles to separate files. "
            + "Messages will reference entities by ID only. "
            + "Only applies to JSON export format."
    )]
    public bool ShouldNormalizeJson { get; init; } = false;

    [CommandOption(
        "fuck-russia",
        EnvironmentVariable = "FUCK_RUSSIA",
        Description = "Don't print the Support Ukraine message to the console.",
        // Use a converter to accept '1' as 'true' to reuse the existing environment variable
        Converter = typeof(TruthyBooleanBindingConverter)
    )]
    public bool IsUkraineSupportMessageDisabled { get; init; } = false;

    [field: AllowNull, MaybeNull]
    protected ChannelExporter Exporter => field ??= new ChannelExporter(Discord);

    protected async ValueTask ExportAsync(IConsole console, IReadOnlyList<Channel> channels)
    {
        var cancellationToken = console.RegisterCancellationHandler();

        // Set up API call statistics tracking if enabled
        var apiStats = ShowStats ? new ApiCallStatistics() : null;
        using var statsScope = apiStats is not null
            ? ScopedApiCallStatistics.Begin(apiStats)
            : null;

        // Asset reuse can only be enabled if the download assets option is set
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/425
        if (ShouldReuseAssets && !ShouldDownloadAssets)
        {
            throw new CommandException("Option --reuse-media cannot be used without --media.");
        }

        // New media file path can only be enabled if the download assets option is set
        if (ShouldUseNestedMediaFilePaths && !ShouldDownloadAssets)
        {
            throw new CommandException("Option --media-nested cannot be used without --media.");
        }

        // Skip bot attachments can only be enabled if the download assets option is set
        if (ShouldSkipBotAttachments && !ShouldDownloadAssets)
        {
            throw new CommandException(
                "Option --skip-bot-attachments cannot be used without --media."
            );
        }

        // Assets directory can only be specified if the download assets option is set
        if (!string.IsNullOrWhiteSpace(AssetsDirPath) && !ShouldDownloadAssets)
        {
            throw new CommandException("Option --media-dir cannot be used without --media.");
        }

        // Normalize JSON can only be enabled for JSON export format
        if (ShouldNormalizeJson && ExportFormat != ExportFormat.Json)
        {
            throw new CommandException(
                "Option --normalize-json can only be used with JSON export format."
            );
        }

        // First day of month mode requires both --after and --before
        if (IsFirstDayOfMonthMode && (After is null || Before is null))
        {
            throw new CommandException(
                "Option --first-day-of-month requires both --after and --before to be specified."
            );
        }

        // Calculate date periods for first-day-of-month mode
        var datePeriods =
            IsFirstDayOfMonthMode && After is not null && Before is not null
                ? DateRangeHelper.GetFirstDayOfMonthRanges(After.Value, Before.Value)
                : null;

        // Validate that at least one period exists in first-day-of-month mode
        if (IsFirstDayOfMonthMode && (datePeriods is null || datePeriods.Count == 0))
        {
            throw new CommandException(
                "No valid first-day-of-month periods found within the specified date range."
            );
        }

        var unwrappedChannels = new List<Channel>(channels);

        // Unwrap threads
        if (ThreadInclusionMode != ThreadInclusionMode.None)
        {
            await console.Output.WriteLineAsync("Fetching threads...");

            var fetchedThreadsCount = 0;
            await console
                .CreateStatusTicker()
                .StartAsync(
                    "...",
                    async ctx =>
                    {
                        await foreach (
                            var thread in Discord.GetChannelThreadsAsync(
                                channels,
                                ThreadInclusionMode == ThreadInclusionMode.All,
                                Before,
                                After,
                                cancellationToken
                            )
                        )
                        {
                            unwrappedChannels.Add(thread);

                            ctx.Status(Markup.Escape($"Fetched '{thread.GetHierarchicalName()}'."));

                            fetchedThreadsCount++;
                        }
                    }
                );

            // Remove forums, as they cannot be exported directly and their constituent threads
            // have already been fetched.
            unwrappedChannels.RemoveAll(channel => channel.Kind == ChannelKind.GuildForum);

            await console.Output.WriteLineAsync($"Fetched {fetchedThreadsCount} thread(s).");
        }

        // Pre-fetch guilds, channels, and roles for all unique guilds to avoid redundant API calls
        var guildIds = unwrappedChannels.Select(c => c.GuildId).Distinct().ToList();
        var guildsById = new ConcurrentDictionary<Snowflake, Guild>();
        var channelsByGuildId = new ConcurrentDictionary<Snowflake, IReadOnlyList<Channel>>();
        var rolesByGuildId = new ConcurrentDictionary<Snowflake, IReadOnlyList<Role>>();

        // Create shared member caches per guild to avoid redundant API calls during parallel exports
        var memberCacheByGuildId = new ConcurrentDictionary<Snowflake, MemberCache>();
        foreach (var guildId in guildIds)
        {
            memberCacheByGuildId[guildId] = new MemberCache(Discord, guildId);
        }

        if (guildIds.Count > 0)
        {
            await console.Output.WriteLineAsync("Fetching guild data...");
            await console
                .CreateStatusTicker()
                .StartAsync(
                    "...",
                    async ctx =>
                    {
                        foreach (var guildId in guildIds)
                        {
                            ctx.Status($"Fetching data for guild {guildId}...");

                            var guild = await Discord.GetGuildAsync(guildId, cancellationToken);
                            guildsById[guildId] = guild;

                            var guildChannels = await Discord
                                .GetGuildChannelsAsync(guildId, cancellationToken)
                                .ToListAsync(cancellationToken);
                            channelsByGuildId[guildId] = guildChannels;

                            var guildRoles = await Discord
                                .GetGuildRolesAsync(guildId, cancellationToken)
                                .ToListAsync(cancellationToken);
                            rolesByGuildId[guildId] = guildRoles;
                        }
                    }
                );
        }

        // Make sure the user does not try to export multiple items into one file.
        // Output path must either be a directory or contain template tokens for this to work.
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/799
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/917
        var willExportMultipleItems =
            unwrappedChannels.Count > 1
            || (IsFirstDayOfMonthMode && datePeriods is not null && datePeriods.Count > 1);

        var isValidOutputPath =
            // Anything is valid when exporting a single item
            !willExportMultipleItems
            // When using template tokens, assume the user knows what they're doing
            || OutputPath.Contains('%')
            // Otherwise, require an existing directory or an unambiguous directory path
            || Directory.Exists(OutputPath)
            || Path.EndsInDirectorySeparator(OutputPath);

        if (!isValidOutputPath)
        {
            throw new CommandException(
                "Attempted to export multiple items, but the output path is neither a directory nor a template. "
                    + "If the provided output path is meant to be treated as a directory, make sure it ends with a slash. "
                    + $"Provided output path: '{OutputPath}'."
            );
        }

        // Build export items (channel + date range pairs)
        // In first-day-of-month mode, each channel is exported multiple times with different date ranges
        var exportItems =
            new List<(Channel Channel, Snowflake? After, Snowflake? Before, string Label)>();

        if (datePeriods is not null)
        {
            // First-day-of-month mode: create an export item for each channel + period combination
            await console.Output.WriteLineAsync(
                $"First-day-of-month mode: exporting {datePeriods.Count} period(s)."
            );

            foreach (var (periodAfter, periodBefore) in datePeriods)
            {
                // Use UTC to match the export filename format
                var periodLabel = periodAfter.ToDate().ToUniversalTime().ToString("yyyy-MM-dd");

                foreach (var channel in unwrappedChannels)
                {
                    if (
                        channel.IsEmpty
                        || !channel.MayHaveMessagesBefore(periodBefore)
                        || !channel.MayHaveMessagesAfter(periodAfter)
                    )
                    {
                        using (console.WithForegroundColor(ConsoleColor.DarkGray))
                            await console.Output.WriteLineAsync(
                                $"Skipping {channel.GetHierarchicalName()} for {periodLabel}"
                            );
                    }
                    else
                    {
                        exportItems.Add(
                            (
                                channel,
                                periodAfter,
                                periodBefore,
                                $"{channel.GetHierarchicalName()} ({periodLabel})"
                            )
                        );
                    }
                }
            }
        }
        else
        {
            // Normal mode: filter channels and create export items with the original date range
            foreach (var channel in unwrappedChannels)
            {
                if (
                    channel.IsEmpty
                    || (Before is not null && !channel.MayHaveMessagesBefore(Before.Value))
                    || (After is not null && !channel.MayHaveMessagesAfter(After.Value))
                )
                {
                    using (console.WithForegroundColor(ConsoleColor.DarkGray))
                        await console.Output.WriteLineAsync(
                            $"Skipping {channel.GetHierarchicalName()}"
                        );
                }
                else
                {
                    exportItems.Add((channel, After, Before, channel.GetHierarchicalName()));
                }
            }
        }

        // Export
        var errorsByExportItem = new ConcurrentDictionary<string, string>();
        var overallStopwatch = Stopwatch.StartNew();

        await console.Output.WriteLineAsync($"Exporting {exportItems.Count} item(s)...");
        await console
            .CreateProgressTicker()
            .HideCompleted(
                // When exporting multiple channels in parallel, hide the completed tasks
                // because it gets hard to visually parse them as they complete out of order.
                // https://github.com/Tyrrrz/DiscordChatExporter/issues/1124
                ParallelLimit > 1
            )
            .StartAsync(async ctx =>
            {
                await Parallel.ForEachAsync(
                    exportItems,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, ParallelLimit),
                        CancellationToken = cancellationToken,
                    },
                    async (exportItem, innerCancellationToken) =>
                    {
                        var (channel, itemAfter, itemBefore, label) = exportItem;
                        try
                        {
                            await ctx.StartTaskAsync(
                                Markup.Escape(label),
                                async progress =>
                                {
                                    // Set up scoped logger for this task (if verbose)
                                    using var _ = IsVerbose
                                        ? ScopedStatusLogger.Begin(
                                            new ProgressTaskStatusLogger(progress)
                                        )
                                        : null;

                                    // Use pre-fetched guild data and shared member cache
                                    var guild = guildsById[channel.GuildId];
                                    var guildChannels = channelsByGuildId.GetValueOrDefault(
                                        channel.GuildId
                                    );
                                    var guildRoles = rolesByGuildId.GetValueOrDefault(
                                        channel.GuildId
                                    );
                                    var memberCache = memberCacheByGuildId.GetValueOrDefault(
                                        channel.GuildId
                                    );

                                    var request = new ExportRequest(
                                        guild,
                                        channel,
                                        OutputPath,
                                        AssetsDirPath,
                                        ExportFormat,
                                        itemAfter,
                                        itemBefore,
                                        PartitionLimit,
                                        MessageFilter,
                                        ShouldFormatMarkdown,
                                        ShouldDownloadAssets,
                                        ShouldSkipBotAttachments,
                                        ShouldReuseAssets,
                                        ShouldUseNestedMediaFilePaths,
                                        Locale,
                                        IsUtcNormalizationEnabled,
                                        ShouldNormalizeJson
                                    );

                                    var messagesExported = await Exporter.ExportChannelAsync(
                                        request,
                                        guildChannels,
                                        guildRoles,
                                        memberCache,
                                        progress.ToPercentageBased(),
                                        innerCancellationToken
                                    );

                                    ScopedStatusLogger.Current.Log(
                                        $"Exported {messagesExported} message(s) from #{channel.Name}"
                                    );
                                }
                            );
                        }
                        catch (ChannelEmptyException)
                        {
                            // Silently skip empty channels
                        }
                        catch (DiscordChatExporterException ex) when (!ex.IsFatal)
                        {
                            errorsByExportItem[label] = ex.Message;
                        }
                    }
                );
            });

        // Stop overall timing
        overallStopwatch.Stop();
        var totalDuration = overallStopwatch.Elapsed;

        // Print the result
        var exportedCount = exportItems.Count - errorsByExportItem.Count;
        using (console.WithForegroundColor(ConsoleColor.White))
        {
            await console.Output.WriteLineAsync(
                $"Successfully exported {exportedCount} item(s) in {FormatDuration(totalDuration)}."
            );
        }

        // Print API statistics if enabled
        if (apiStats is not null && apiStats.HasCalls)
        {
            await console.Output.WriteLineAsync();
            await console.Output.WriteLineAsync(apiStats.GetSummary());
        }

        // Print errors
        if (errorsByExportItem.Any())
        {
            await console.Output.WriteLineAsync();

            using (console.WithForegroundColor(ConsoleColor.Red))
            {
                await console.Error.WriteLineAsync("Failed to export the following item(s):");
            }

            foreach (var (label, message) in errorsByExportItem)
            {
                await console.Error.WriteAsync($"{label}: ");
                using (console.WithForegroundColor(ConsoleColor.Red))
                    await console.Error.WriteLineAsync(message);
            }

            await console.Error.WriteLineAsync();
        }

        // Fail the command only if ALL items failed to export.
        // If only some items failed to export, it's okay.
        if (exportedCount <= 0 && errorsByExportItem.Any())
            throw new CommandException("Export failed.");
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        if (duration.TotalSeconds >= 1)
            return $"{duration.TotalSeconds:F1}s";
        return $"{duration.TotalMilliseconds:F0}ms";
    }

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        // Support Ukraine callout
        if (!IsUkraineSupportMessageDisabled)
        {
            console.Output.WriteLine(
                "┌────────────────────────────────────────────────────────────────────┐"
            );
            console.Output.WriteLine(
                "│   Thank you for supporting Ukraine <3                              │"
            );
            console.Output.WriteLine(
                "│                                                                    │"
            );
            console.Output.WriteLine(
                "│   As Russia wages a genocidal war against my country,              │"
            );
            console.Output.WriteLine(
                "│   I'm grateful to everyone who continues to                        │"
            );
            console.Output.WriteLine(
                "│   stand with Ukraine in our fight for freedom.                     │"
            );
            console.Output.WriteLine(
                "│                                                                    │"
            );
            console.Output.WriteLine(
                "│   Learn more: https://tyrrrz.me/ukraine                            │"
            );
            console.Output.WriteLine(
                "└────────────────────────────────────────────────────────────────────┘"
            );
            console.Output.WriteLine("");
        }

        await base.ExecuteAsync(console);
    }
}
