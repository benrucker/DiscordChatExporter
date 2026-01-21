using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Utils;
using DiscordChatExporter.Core.Utils.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal partial class ExportAssetDownloader(
    string workingDirPath,
    bool reuse,
    bool useNestedMediaFilePaths
)
{
    private static readonly AsyncKeyedLocker<string> Locker = new();

    // Use scoped logger so each export task can have its own logger
    private static IStatusLogger Log => ScopedStatusLogger.Current;

    // File paths of the previously downloaded assets
    private readonly Dictionary<string, string> _previousPathsByUrl = new(StringComparer.Ordinal);

    public async ValueTask<string> DownloadAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        var localFilePath = useNestedMediaFilePaths
            ? GetFilePathFromUrl(url)
            : GetFileNameFromUrlLegacy(url);
        var filePath = Path.Combine(workingDirPath, localFilePath);

        using var _ = await Locker.LockAsync(filePath, cancellationToken);

        if (_previousPathsByUrl.TryGetValue(url, out var cachedFilePath))
            return cachedFilePath;

        // Reuse existing files if we're allowed to
        if (reuse && File.Exists(filePath))
            return _previousPathsByUrl[url] = filePath;

        Directory.CreateDirectory(workingDirPath);

        // Extract a short identifier for logging (last path segment or truncated URL)
        var assetName =
            url.Length > 50 ? url.Substring(url.LastIndexOf('/') + 1).Split('?')[0] : url;
        if (assetName.Length > 40)
            assetName = assetName.Substring(0, 37) + "...";

        Log.Log($"Downloading asset: {assetName}");

        await Http.ResiliencePipeline.ExecuteAsync(
            async innerCancellationToken =>
            {
                // Download the file
                using var response = await Http.Client.GetAsync(url, innerCancellationToken);
                var directory = Path.GetDirectoryName(filePath);
                if (directory != null)
                    Directory.CreateDirectory(directory);
                await using var output = File.Create(filePath);
                await response.Content.CopyToAsync(output, innerCancellationToken);
            },
            cancellationToken
        );

        return _previousPathsByUrl[url] = filePath;
    }
}

internal partial class ExportAssetDownloader
{
    private static string GetFilePathFromUrl(string url)
    {
        var uri = new Uri(NormalizeUrl(url));

        // If this isn't a Discord CDN URL, save the file to the external folder.
        if (!string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase))
        {
            return GetExternalFilePath(uri);
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return GetExternalFilePath(uri);
        }

        var resourceType = segments[0];
        string uniqueId;
        string fileExtension;

        switch (resourceType)
        {
            // emojis/{emoji_id}.png
            case "emojis":
            // stickers/{sticker_id}.png
            case "stickers":
            {
                // ID is the filename without extension
                var fileName = segments[1];
                uniqueId = Path.GetFileNameWithoutExtension(fileName);
                fileExtension = Path.GetExtension(fileName);
                break;
            }

            // attachments/{channel_id}/{attachment_id}/{filename}
            case "attachments":
            {
                if (segments.Length < 4)
                    return GetExternalFilePath(uri);
                uniqueId = segments[2]; // attachment_id is globally unique
                fileExtension = Path.GetExtension(segments[3]);
                break;
            }

            // icons/{guild_id}/{guild_icon_hash}.png
            case "icons":
            {
                if (segments.Length < 3)
                    return GetExternalFilePath(uri);
                var iconFileName = segments[2];
                var iconHash = Path.GetFileNameWithoutExtension(iconFileName);
                uniqueId = $"{segments[1]}_{iconHash}"; // guild_id + icon hash
                fileExtension = Path.GetExtension(iconFileName);
                break;
            }

            // guilds/{guild_id}/users/{user_id}/avatars/{member_avatar_hash}.png
            case "guilds":
            {
                if (segments.Length < 6 || segments[2] != "users" || segments[4] != "avatars")
                    return GetExternalFilePath(uri);
                var avatarFileName = segments[5];
                var avatarHash = Path.GetFileNameWithoutExtension(avatarFileName);
                uniqueId = $"{segments[3]}_{avatarHash}"; // user_id + avatar hash
                fileExtension = Path.GetExtension(avatarFileName);
                resourceType = "avatars"; // Save under avatars/ instead of guilds/
                break;
            }

            // Unrecognized Discord CDN URL - preserve original path structure
            default:
            {
                var path = string.Join(
                    Path.DirectorySeparatorChar.ToString(),
                    segments[..^1].Select(Path.EscapeFileName)
                );
                var fileName = Path.EscapeFileName(segments[^1]);
                return Path.Combine(path, fileName);
            }
        }

        // If extension is suspiciously long (>10 chars), it's probably not a real extension
        if (fileExtension.Length > 10)
        {
            fileExtension = "";
        }

        return Path.Combine(resourceType, $"{uniqueId}{fileExtension}");
    }

    // Remove signature parameters from Discord CDN URLs to normalize them
    private static string NormalizeUrl(string url)
    {
        var uri = new Uri(url);
        if (!string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase))
            return url;

        var query = HttpUtility.ParseQueryString(uri.Query);
        query.Remove("ex");
        query.Remove("is");
        query.Remove("hm");

        var queryString = query.ToString();
        if (string.IsNullOrEmpty(queryString))
            return uri.GetLeftPart(UriPartial.Path);

        return $"{uri.GetLeftPart(UriPartial.Path)}?{queryString}";
    }

    private static string GetExternalFilePath(Uri uri)
    {
        // For external URLs, use full SHA256 hash of the URL to guarantee uniqueness.
        // URLs can be up to 2000+ characters, far exceeding filesystem limits,
        // so we hash them for safe storage while maintaining zero collision risk.

        // Normalize the URL for hashing (remove volatile query params from Discord proxies)
        var urlToHash = uri.ToString();

        // Compute full SHA256 hash (64 hex chars) - no truncation to prevent collisions
        var hash = SHA256
            .HashData(Encoding.UTF8.GetBytes(urlToHash))
            .Pipe(Convert.ToHexStringLower);

        // Extract file extension from the URL path
        var fileName = Path.GetFileName(uri.AbsolutePath);
        var fileExtension = Path.GetExtension(fileName);

        // If extension is suspiciously long (>10 chars), it's probably not a real extension
        if (fileExtension.Length > 10)
        {
            fileExtension = "";
        }

        // Sanitize domain for use as directory name
        var domain = Path.EscapeFileName(uri.Host);

        // Save as: external/{domain}/{full_sha256_hash}.{ext}
        return Path.Combine("external", domain, $"{hash}{fileExtension}");
    }
}

internal partial class ExportAssetDownloader
{
    private static string GetUrlHash(string url)
    {
        // Remove signature parameters from Discord CDN URLs to normalize them
        static string NormalizeUrl(string url)
        {
            var uri = new Uri(url);
            if (!string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase))
                return url;

            var query = HttpUtility.ParseQueryString(uri.Query);
            query.Remove("ex");
            query.Remove("is");
            query.Remove("hm");

            return uri.GetLeftPart(UriPartial.Path) + query;
        }

        return SHA256
            .HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)))
            .Pipe(Convert.ToHexStringLower)
            // 5 chars ought to be enough for anybody
            .Truncate(5);
    }

    private static string GetFileNameFromUrlLegacy(string url)
    {
        var urlHash = GetUrlHash(url);

        // Try to extract the file name from URL
        var fileName = Regex.Match(url, @".+/([^?]*)").Groups[1].Value;

        // If it's not there, just use the URL hash as the file name
        if (string.IsNullOrWhiteSpace(fileName))
            return urlHash;

        // Otherwise, use the original file name but inject the hash in the middle
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var fileExtension = Path.GetExtension(fileName);

        // Probably not a file extension, just a dot in a long file name
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/812
        if (fileExtension.Length > 41)
        {
            fileNameWithoutExtension = fileName;
            fileExtension = "";
        }

        return Path.EscapeFileName(
            fileNameWithoutExtension.Truncate(42) + '-' + urlHash + fileExtension
        );
    }
}
