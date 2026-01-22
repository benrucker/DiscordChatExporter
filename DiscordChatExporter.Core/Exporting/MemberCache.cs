using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;

namespace DiscordChatExporter.Core.Exporting;

/// <summary>
/// Thread-safe cache for guild members that can be shared across parallel channel exports.
/// This prevents redundant API calls when the same users are mentioned across multiple channels.
/// </summary>
public class MemberCache(DiscordClient discord, Snowflake guildId)
{
    // Cache stores Member? to also cache negative results (users who left the guild)
    private readonly ConcurrentDictionary<Snowflake, Member?> _membersById = new();

    // Track in-flight requests to avoid duplicate API calls for the same member
    private readonly ConcurrentDictionary<Snowflake, Task<Member?>> _pendingRequests = new();

    /// <summary>
    /// Gets a member from the cache, fetching from API if not already cached.
    /// Thread-safe and handles concurrent requests for the same member.
    /// </summary>
    public async ValueTask<Member?> GetMemberAsync(
        Snowflake memberId,
        User? fallbackUser = null,
        CancellationToken cancellationToken = default
    )
    {
        // Fast path: already cached
        if (_membersById.TryGetValue(memberId, out var cachedMember))
            return cachedMember;

        // Slow path: need to fetch from API
        // Use GetOrAdd with a Task to ensure only one request per member
        var fetchTask = _pendingRequests.GetOrAdd(
            memberId,
            _ => FetchMemberAsync(memberId, fallbackUser, cancellationToken)
        );

        try
        {
            return await fetchTask;
        }
        finally
        {
            // Clean up pending request after completion
            _pendingRequests.TryRemove(memberId, out _);
        }
    }

    private async Task<Member?> FetchMemberAsync(
        Snowflake memberId,
        User? fallbackUser,
        CancellationToken cancellationToken
    )
    {
        // Double-check cache in case another task populated it
        if (_membersById.TryGetValue(memberId, out var cachedMember))
            return cachedMember;

        var member = await discord.TryGetGuildMemberAsync(guildId, memberId, cancellationToken);

        // User may have left the guild since they were mentioned.
        // Create a dummy member object based on the user info.
        if (member is null)
        {
            var user = fallbackUser ?? await discord.TryGetUserAsync(memberId, cancellationToken);

            // User may have been deleted since they were mentioned
            if (user is not null)
                member = Member.CreateFallback(user);
        }

        // Store the result even if it's null, to avoid re-fetching non-existing members
        _membersById[memberId] = member;

        return member;
    }

    /// <summary>
    /// Tries to get a member from the cache without making an API call.
    /// Returns null if the member is not in the cache.
    /// </summary>
    public Member? TryGetMember(Snowflake memberId) =>
        _membersById.TryGetValue(memberId, out var member) ? member : null;

    /// <summary>
    /// Gets statistics about the cache.
    /// </summary>
    public int CachedMemberCount => _membersById.Count;
}
