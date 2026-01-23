using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Utils;
using DiscordChatExporter.Core.Utils.Extensions;

namespace DiscordChatExporter.Core.Exporting;

/// <summary>
/// Result of resolving an asset URL, including download status.
/// </summary>
internal record AssetResolveResult(string Url, bool WasSkipped = false, bool WasFailed = false);

internal class ExportContext(
    DiscordClient discord,
    ExportRequest request,
    MemberCache? sharedMemberCache = null
)
{
    // Local member cache for this context (used for TryGetMember lookups)
    private readonly Dictionary<Snowflake, Member?> _membersById = new();
    private readonly Dictionary<Snowflake, Channel> _channelsById = new();
    private readonly Dictionary<Snowflake, Role> _rolesById = new();

    private readonly ExportAssetDownloader _assetDownloader = new(
        request.AssetsDirPath,
        request.ShouldReuseAssets,
        request.ShouldUseNestedMediaFilePaths
    );

    public DiscordClient Discord { get; } = discord;

    public ExportRequest Request { get; } = request;

    public DateTimeOffset NormalizeDate(DateTimeOffset instant) =>
        Request.IsUtcNormalizationEnabled ? instant.ToUniversalTime() : instant.ToLocalTime();

    public string FormatDate(DateTimeOffset instant, string format = "g") =>
        NormalizeDate(instant).ToString(format, Request.CultureInfo);

    public void PopulateChannelsAndRoles(IEnumerable<Channel> channels, IEnumerable<Role> roles)
    {
        foreach (var channel in channels)
            _channelsById[channel.Id] = channel;

        foreach (var role in roles)
            _rolesById[role.Id] = role;
    }

    public async ValueTask PopulateChannelsAndRolesAsync(
        CancellationToken cancellationToken = default
    )
    {
        // Skip if already populated
        if (_channelsById.Count > 0 || _rolesById.Count > 0)
            return;

        await foreach (
            var channel in Discord.GetGuildChannelsAsync(Request.Guild.Id, cancellationToken)
        )
        {
            _channelsById[channel.Id] = channel;
        }

        await foreach (var role in Discord.GetGuildRolesAsync(Request.Guild.Id, cancellationToken))
        {
            _rolesById[role.Id] = role;
        }
    }

    // Because members cannot be pulled in bulk, we need to populate them on demand
    private async ValueTask PopulateMemberAsync(
        Snowflake id,
        User? fallbackUser,
        CancellationToken cancellationToken = default
    )
    {
        if (_membersById.ContainsKey(id))
            return;

        Member? member;

        // Use shared cache if available (for parallel exports)
        if (sharedMemberCache is not null)
        {
            member = await sharedMemberCache.GetMemberAsync(id, fallbackUser, cancellationToken);
        }
        else
        {
            // Fallback to direct API call (single channel export)
            member = await Discord.TryGetGuildMemberAsync(Request.Guild.Id, id, cancellationToken);

            // User may have left the guild since they were mentioned.
            // Create a dummy member object based on the user info.
            if (member is null)
            {
                var user = fallbackUser ?? await Discord.TryGetUserAsync(id, cancellationToken);

                // User may have been deleted since they were mentioned
                if (user is not null)
                    member = Member.CreateFallback(user);
            }
        }

        // Store in local cache for TryGetMember lookups within this context
        _membersById[id] = member;
    }

    public async ValueTask PopulateMemberAsync(
        Snowflake id,
        CancellationToken cancellationToken = default
    ) => await PopulateMemberAsync(id, null, cancellationToken);

    public async ValueTask PopulateMemberAsync(
        User user,
        CancellationToken cancellationToken = default
    ) => await PopulateMemberAsync(user.Id, user, cancellationToken);

    public Member? TryGetMember(Snowflake id) => _membersById.GetValueOrDefault(id);

    public Channel? TryGetChannel(Snowflake id) => _channelsById.GetValueOrDefault(id);

    public Role? TryGetRole(Snowflake id) => _rolesById.GetValueOrDefault(id);

    public IReadOnlyList<Role> GetUserRoles(Snowflake id) =>
        TryGetMember(id)
            ?.RoleIds.Select(TryGetRole)
            .WhereNotNull()
            .OrderByDescending(r => r.Position)
            .ToArray()
        ?? [];

    public Color? TryGetUserColor(Snowflake id) =>
        GetUserRoles(id).Where(r => r.Color is not null).Select(r => r.Color).FirstOrDefault();

    /// <summary>
    /// Resolves an asset URL, downloading the asset if configured to do so.
    /// Returns both the resolved URL and download status information.
    /// </summary>
    public async ValueTask<AssetResolveResult> ResolveAssetUrlWithStatusAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        if (!Request.ShouldDownloadAssets)
            return new AssetResolveResult(url);

        try
        {
            var result = await _assetDownloader.DownloadAsync(url, cancellationToken);

            // Return original URL for skipped/failed downloads
            if (result.WasSkipped)
                return new AssetResolveResult(url, WasSkipped: true);

            if (result.WasFailed)
                return new AssetResolveResult(url, WasFailed: true);

            var filePath = result.FilePath;
            var relativeFilePath = Path.GetRelativePath(Request.OutputDirPath, filePath);

            // Prefer the relative path so that the export package can be copied around without breaking references.
            // However, if the assets directory lies outside the export directory, use the absolute path instead.
            var shouldUseAbsoluteFilePath =
                relativeFilePath.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                )
                || relativeFilePath.StartsWith(
                    ".." + Path.AltDirectorySeparatorChar,
                    StringComparison.Ordinal
                );

            var optimalFilePath = shouldUseAbsoluteFilePath ? filePath : relativeFilePath;

            // For HTML, the path needs to be properly formatted
            if (Request.Format is ExportFormat.HtmlDark or ExportFormat.HtmlLight)
                return new AssetResolveResult(Url.EncodeFilePath(optimalFilePath));

            return new AssetResolveResult(optimalFilePath);
        }
        // Try to catch only exceptions related to failed HTTP requests
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/332
        // https://github.com/Tyrrrz/DiscordChatExporter/issues/372
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // We don't want this to crash the exporting process in case of failure.
            return new AssetResolveResult(url, WasFailed: true);
        }
    }

    /// <summary>
    /// Resolves an asset URL, downloading the asset if configured to do so.
    /// Returns just the resolved URL (for backward compatibility).
    /// </summary>
    public async ValueTask<string> ResolveAssetUrlAsync(
        string url,
        CancellationToken cancellationToken = default
    ) => (await ResolveAssetUrlWithStatusAsync(url, cancellationToken)).Url;
}
