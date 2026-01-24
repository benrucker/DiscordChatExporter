using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Utils.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal class NormalizedJsonAuxiliaryWriter : IAsyncDisposable
{
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private readonly ExportContext _context;
    private readonly Dictionary<Snowflake, User> _encounteredUsers = new();
    private readonly Dictionary<Snowflake, Member> _encounteredMembers = new();
    private readonly Dictionary<Snowflake, Role> _encounteredRoles = new();

    public NormalizedJsonAuxiliaryWriter(ExportContext context)
    {
        _context = context;
    }

    public void TrackUser(User user)
    {
        var member = _context.TryGetMember(user.Id);
        if (member is not null)
        {
            _encounteredMembers[user.Id] = member;

            // Also track roles for this member
            foreach (var role in _context.GetUserRoles(user.Id))
            {
                _encounteredRoles[role.Id] = role;
            }
        }
        else
        {
            // Fallback to just user data
            _encounteredUsers[user.Id] = user;
        }
    }

    /// <summary>
    /// Flushes accumulated user/member/role data to auxiliary files.
    /// Called at partition boundaries to ensure data is not lost on crash.
    /// </summary>
    public async ValueTask FlushAsync()
    {
        // Skip if nothing to flush
        if (
            _encounteredUsers.Count == 0
            && _encounteredMembers.Count == 0
            && _encounteredRoles.Count == 0
        )
            return;

        var isDirect = _context.Request.Channel.IsDirect;
        var dirPath = _context.Request.OutputDirPath;

        if (isDirect)
        {
            await WriteUsersFileAsync(dirPath);
        }
        else
        {
            await WriteMembersFileAsync(dirPath);
            await WriteRolesFileAsync(dirPath);
        }

        // Clear tracked data after successful flush
        // (the data is now persisted, no need to re-write on next flush)
        _encounteredUsers.Clear();
        _encounteredMembers.Clear();
        _encounteredRoles.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        // Final flush of any remaining tracked data
        await FlushAsync();
    }

    private async Task WriteUsersFileAsync(string dirPath)
    {
        var filePath = Path.Combine(dirPath, "users.json");
        var existing = await LoadExistingUsersAsync(filePath);

        // Add users that we couldn't get member data for
        foreach (var (id, user) in _encounteredUsers)
        {
            existing[id] = user;
        }

        // Also add any members as users (DM context, but might have cached member info)
        foreach (var (id, member) in _encounteredMembers)
        {
            existing[id] = member.User;
        }

        await WriteUsersToFileAsync(filePath, existing);
    }

    private async Task WriteMembersFileAsync(string dirPath)
    {
        var filePath = Path.Combine(dirPath, "members.json");
        var (existingMembers, existingUsers) = await LoadExistingMembersAsync(filePath);

        foreach (var (id, member) in _encounteredMembers)
        {
            existingMembers[id] = member;
            existingUsers.Remove(id); // Remove from users if now we have member data
        }

        // Also add any users that we couldn't get member data for
        foreach (var (id, user) in _encounteredUsers)
        {
            if (!existingMembers.ContainsKey(id))
            {
                existingUsers[id] = user;
            }
        }

        await WriteMembersToFileAsync(filePath, existingMembers, existingUsers);
    }

    private async Task WriteRolesFileAsync(string dirPath)
    {
        var filePath = Path.Combine(dirPath, "roles.json");
        var existing = await LoadExistingRolesAsync(filePath);

        foreach (var (id, role) in _encounteredRoles)
        {
            existing[id] = role;
        }

        await WriteRolesToFileAsync(filePath, existing);
    }

    private static async Task<Dictionary<Snowflake, User>> LoadExistingUsersAsync(string filePath)
    {
        await FileLock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
                return new Dictionary<Snowflake, User>();

            try
            {
                await using var stream = File.OpenRead(filePath);
                using var doc = await JsonDocument.ParseAsync(stream);
                var result = new Dictionary<Snowflake, User>();

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var id = Snowflake.Parse(prop.Name);
                    var obj = prop.Value;

                    var user = new User(
                        id,
                        obj.TryGetProperty("isBot", out var isBot) && isBot.GetBoolean(),
                        obj.TryGetProperty("discriminator", out var disc)
                            ? int.TryParse(disc.GetString(), out var d) && d != 0
                                ? d
                                : null
                            : null,
                        obj.GetProperty("name").GetString() ?? "",
                        obj.TryGetProperty("displayName", out var dn)
                            ? dn.GetString() ?? ""
                            : obj.GetProperty("name").GetString() ?? "",
                        obj.TryGetProperty("avatarUrl", out var av) ? av.GetString() ?? "" : ""
                    );
                    result[id] = user;
                }

                return result;
            }
            catch
            {
                return new Dictionary<Snowflake, User>();
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task<(
        Dictionary<Snowflake, Member>,
        Dictionary<Snowflake, User>
    )> LoadExistingMembersAsync(string filePath)
    {
        await FileLock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
                return (new Dictionary<Snowflake, Member>(), new Dictionary<Snowflake, User>());

            try
            {
                await using var stream = File.OpenRead(filePath);
                using var doc = await JsonDocument.ParseAsync(stream);
                var members = new Dictionary<Snowflake, Member>();
                var users = new Dictionary<Snowflake, User>();

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var id = Snowflake.Parse(prop.Name);
                    var obj = prop.Value;

                    var user = new User(
                        id,
                        obj.TryGetProperty("isBot", out var isBot) && isBot.GetBoolean(),
                        obj.TryGetProperty("discriminator", out var disc)
                            ? int.TryParse(disc.GetString(), out var d) && d != 0
                                ? d
                                : null
                            : null,
                        obj.GetProperty("name").GetString() ?? "",
                        obj.TryGetProperty("displayName", out var dn)
                            ? dn.GetString() ?? ""
                            : obj.GetProperty("name").GetString() ?? "",
                        obj.TryGetProperty("avatarUrl", out var av) ? av.GetString() ?? "" : ""
                    );

                    // Check if this entry has roleIds - if so, it's a member
                    if (obj.TryGetProperty("roleIds", out var roleIds))
                    {
                        var roleIdList = new List<Snowflake>();
                        foreach (var roleId in roleIds.EnumerateArray())
                        {
                            if (roleId.GetString() is { } rid)
                                roleIdList.Add(Snowflake.Parse(rid));
                        }

                        var displayName = obj.TryGetProperty("displayName", out var memberDn)
                            ? memberDn.GetString()
                            : null;
                        var avatarUrl = obj.TryGetProperty("avatarUrl", out var memberAv)
                            ? memberAv.GetString()
                            : null;

                        members[id] = new Member(user, displayName, avatarUrl, roleIdList);
                    }
                    else
                    {
                        users[id] = user;
                    }
                }

                return (members, users);
            }
            catch
            {
                return (new Dictionary<Snowflake, Member>(), new Dictionary<Snowflake, User>());
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task<Dictionary<Snowflake, Role>> LoadExistingRolesAsync(string filePath)
    {
        await FileLock.WaitAsync();
        try
        {
            if (!File.Exists(filePath))
                return new Dictionary<Snowflake, Role>();

            try
            {
                await using var stream = File.OpenRead(filePath);
                using var doc = await JsonDocument.ParseAsync(stream);
                var result = new Dictionary<Snowflake, Role>();

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var id = Snowflake.Parse(prop.Name);
                    var obj = prop.Value;

                    var name = obj.GetProperty("name").GetString() ?? "";
                    var position = obj.TryGetProperty("position", out var pos) ? pos.GetInt32() : 0;

                    System.Drawing.Color? color = null;
                    if (
                        obj.TryGetProperty("color", out var colorProp)
                        && colorProp.GetString() is { } colorStr
                        && colorStr.StartsWith('#')
                    )
                    {
                        var rgb = Convert.ToInt32(colorStr[1..], 16);
                        color = System.Drawing.Color.FromArgb(rgb).ResetAlpha();
                    }

                    result[id] = new Role(id, name, position, color);
                }

                return result;
            }
            catch
            {
                return new Dictionary<Snowflake, Role>();
            }
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task WriteUsersToFileAsync(
        string filePath,
        Dictionary<Snowflake, User> users
    )
    {
        await FileLock.WaitAsync();
        try
        {
            await using var stream = File.Create(filePath);
            await using var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    Indented = true,
                }
            );

            writer.WriteStartObject();

            foreach (var (id, user) in users)
            {
                writer.WritePropertyName(id.ToString());
                writer.WriteStartObject();
                writer.WriteString("id", id.ToString());
                writer.WriteString("name", user.Name);
                writer.WriteString("discriminator", user.DiscriminatorFormatted);
                writer.WriteString("displayName", user.DisplayName);
                writer.WriteBoolean("isBot", user.IsBot);
                writer.WriteString("avatarUrl", user.AvatarUrl);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            await writer.FlushAsync();
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task WriteMembersToFileAsync(
        string filePath,
        Dictionary<Snowflake, Member> members,
        Dictionary<Snowflake, User> fallbackUsers
    )
    {
        await FileLock.WaitAsync();
        try
        {
            await using var stream = File.Create(filePath);
            await using var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    Indented = true,
                }
            );

            writer.WriteStartObject();

            foreach (var (id, member) in members)
            {
                writer.WritePropertyName(id.ToString());
                writer.WriteStartObject();
                writer.WriteString("id", id.ToString());
                writer.WriteString("name", member.User.Name);
                writer.WriteString("discriminator", member.User.DiscriminatorFormatted);
                writer.WriteString("displayName", member.DisplayName ?? member.User.DisplayName);
                writer.WriteBoolean("isBot", member.User.IsBot);
                writer.WriteString("avatarUrl", member.AvatarUrl ?? member.User.AvatarUrl);

                writer.WriteStartArray("roleIds");
                foreach (var roleId in member.RoleIds)
                {
                    writer.WriteStringValue(roleId.ToString());
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            // Write fallback users (without roleIds)
            foreach (var (id, user) in fallbackUsers)
            {
                writer.WritePropertyName(id.ToString());
                writer.WriteStartObject();
                writer.WriteString("id", id.ToString());
                writer.WriteString("name", user.Name);
                writer.WriteString("discriminator", user.DiscriminatorFormatted);
                writer.WriteString("displayName", user.DisplayName);
                writer.WriteBoolean("isBot", user.IsBot);
                writer.WriteString("avatarUrl", user.AvatarUrl);
                writer.WriteStartArray("roleIds");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            await writer.FlushAsync();
        }
        finally
        {
            FileLock.Release();
        }
    }

    private static async Task WriteRolesToFileAsync(
        string filePath,
        Dictionary<Snowflake, Role> roles
    )
    {
        await FileLock.WaitAsync();
        try
        {
            await using var stream = File.Create(filePath);
            await using var writer = new Utf8JsonWriter(
                stream,
                new JsonWriterOptions
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    Indented = true,
                }
            );

            writer.WriteStartObject();

            foreach (var (id, role) in roles)
            {
                writer.WritePropertyName(id.ToString());
                writer.WriteStartObject();
                writer.WriteString("id", id.ToString());
                writer.WriteString("name", role.Name);
                writer.WriteString("color", role.Color?.ToHex());
                writer.WriteNumber("position", role.Position);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            await writer.FlushAsync();
        }
        finally
        {
            FileLock.Release();
        }
    }
}
