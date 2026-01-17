using System;
using System.Collections.Generic;
using DiscordChatExporter.Core.Discord;

namespace DiscordChatExporter.Cli.Commands.Shared;

public static class DateRangeHelper
{
    /// <summary>
    /// Generates date ranges for the first day of each month within the specified range.
    /// Each range covers one full day (00:00:00 to start of next day).
    /// </summary>
    public static IReadOnlyList<(Snowflake After, Snowflake Before)> GetFirstDayOfMonthRanges(
        Snowflake after,
        Snowflake before
    )
    {
        var ranges = new List<(Snowflake After, Snowflake Before)>();

        var afterDate = after.ToDate();
        var beforeDate = before.ToDate();

        // Start with the first day of the month after the --after date
        // If --after is on the 1st, we start with the following month
        var currentDate = new DateTimeOffset(
            afterDate.Year,
            afterDate.Month,
            1,
            0,
            0,
            0,
            TimeSpan.Zero
        );

        // Move to the next month if --after is on or after the 1st of the month
        if (afterDate.Day >= 1)
        {
            currentDate = currentDate.AddMonths(1);
        }

        while (currentDate < beforeDate)
        {
            var dayStart = currentDate;
            var dayEnd = currentDate.AddDays(1);

            // Only include this period if the full day is within the before date
            if (dayEnd <= beforeDate || dayStart < beforeDate)
            {
                ranges.Add((Snowflake.FromDate(dayStart), Snowflake.FromDate(dayEnd)));
            }

            currentDate = currentDate.AddMonths(1);
        }

        return ranges;
    }
}
