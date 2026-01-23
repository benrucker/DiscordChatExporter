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

/// <summary>
/// Result of an asset download operation.
/// </summary>
internal record AssetDownloadResult(
    string FilePath,
    bool WasSkipped = false,
    bool WasFailed = false
)
{
    /// <summary>
    /// Creates a result for a successfully downloaded asset.
    /// </summary>
    public static AssetDownloadResult Success(string filePath) => new(filePath);

    /// <summary>
    /// Creates a result for a skipped download (embed-only URL, proxy thumbnail, etc.).
    /// </summary>
    public static AssetDownloadResult Skipped(string originalUrl) =>
        new(originalUrl, WasSkipped: true);

    /// <summary>
    /// Creates a result for a failed download (invalid resource, wrong content type, etc.).
    /// </summary>
    public static AssetDownloadResult Failed(string originalUrl) =>
        new(originalUrl, WasFailed: true);
}

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

    public async ValueTask<AssetDownloadResult> DownloadAsync(
        string url,
        CancellationToken cancellationToken = default
    )
    {
        // Check if this URL should be skipped entirely (embed-only sites, proxy thumbnails)
        if (ShouldSkipDownload(url))
        {
            return AssetDownloadResult.Skipped(url);
        }

        var localFilePath = useNestedMediaFilePaths
            ? GetFilePathFromUrl(url)
            : GetFileNameFromUrlLegacy(url);
        var filePath = Path.Combine(workingDirPath, localFilePath);

        using var _ = await Locker.LockAsync(filePath, cancellationToken);

        if (_previousPathsByUrl.TryGetValue(url, out var cachedFilePath))
            return AssetDownloadResult.Success(cachedFilePath);

        // Reuse existing files if we're allowed to
        if (reuse && File.Exists(filePath))
        {
            _previousPathsByUrl[url] = filePath;
            return AssetDownloadResult.Success(filePath);
        }

        Directory.CreateDirectory(workingDirPath);

        // Extract a short identifier for logging (last path segment or truncated URL)
        var assetName =
            url.Length > 50 ? url.Substring(url.LastIndexOf('/') + 1).Split('?')[0] : url;
        if (assetName.Length > 40)
            assetName = assetName.Substring(0, 37) + "...";

        Log.Log($"Downloading asset: {assetName}");

        string? finalFilePath = null;
        var downloadFailed = false;

        await Http.ResiliencePipeline.ExecuteAsync(
            async innerCancellationToken =>
            {
                // Download the file to memory first to check content
                using var response = await Http.Client.GetAsync(url, innerCancellationToken);
                var content = await response.Content.ReadAsByteArrayAsync(innerCancellationToken);
                var contentType = response.Content.Headers.ContentType?.MediaType;

                // Check for invalid resource responses
                if (IsInvalidResourceResponse(content, contentType))
                {
                    Log.Log($"Invalid resource response for: {assetName}");
                    downloadFailed = true;
                    return;
                }

                // Determine the final file path, potentially adding extension from Content-Type
                var currentFilePath = filePath;
                var currentExtension = Path.GetExtension(currentFilePath);

                // If no extension or very short extension, try to get from Content-Type
                if (string.IsNullOrEmpty(currentExtension) || currentExtension.Length <= 1)
                {
                    var extensionFromContentType = GetExtensionFromContentType(contentType);
                    if (!string.IsNullOrEmpty(extensionFromContentType))
                    {
                        currentFilePath = filePath + extensionFromContentType;
                    }
                }

                var directory = Path.GetDirectoryName(currentFilePath);
                if (directory != null)
                    Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(currentFilePath, content, innerCancellationToken);
                finalFilePath = currentFilePath;
            },
            cancellationToken
        );

        if (downloadFailed || finalFilePath is null)
        {
            return AssetDownloadResult.Failed(url);
        }

        _previousPathsByUrl[url] = finalFilePath;
        return AssetDownloadResult.Success(finalFilePath);
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
        // Special case: Twemoji URLs from jsdelivr CDN
        // These are static files that don't need hashing - preserve original path structure
        if (
            uri.Host.Equals("cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/twemoji", StringComparison.OrdinalIgnoreCase)
        )
        {
            // Preserve as: twemoji/{filename}.svg
            var twemojiFileName = Path.GetFileName(uri.AbsolutePath);
            return Path.Combine("twemoji", Path.EscapeFileName(twemojiFileName));
        }

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

internal partial class ExportAssetDownloader
{
    /// <summary>
    /// Checks if a URL points to a site that only provides embed HTML pages, not downloadable media.
    /// </summary>
    private static bool IsEmbedOnlyUrl(Uri uri)
    {
        return uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("twitter.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("x.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("soundcloud.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("spotify.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("vimeo.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a URL points to Discord's image proxy thumbnails which are regenerated on demand.
    /// </summary>
    private static bool IsProxyThumbnailUrl(Uri uri)
    {
        // Discord's image proxy for external thumbnails (e.g., images-ext-1.discordapp.net)
        return uri.Host.StartsWith("images-ext", StringComparison.OrdinalIgnoreCase)
            && uri.Host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a URL should be skipped for downloading.
    /// </summary>
    private static bool ShouldSkipDownload(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return IsProxyThumbnailUrl(uri) || IsEmbedOnlyUrl(uri);
    }

    /// <summary>
    /// Maps Content-Type header values to file extensions.
    /// </summary>
    private static string? GetExtensionFromContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return null;

        // Extract just the media type without parameters (e.g., "image/png; charset=utf-8" -> "image/png")
        var mediaType = contentType.Split(';')[0].Trim().ToLowerInvariant();

        return mediaType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/avif" => ".avif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/quicktime" => ".mov",
            "audio/mpeg" => ".mp3",
            "audio/ogg" => ".ogg",
            "audio/wav" => ".wav",
            "audio/webm" => ".weba",
            "audio/flac" => ".flac",
            "application/json" => ".json",
            _ => null,
        };
    }

    /// <summary>
    /// Detects if downloaded content is an invalid resource response instead of actual media.
    /// </summary>
    private static bool IsInvalidResourceResponse(byte[] content, string? contentType)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            var mediaType = contentType.Split(';')[0].Trim().ToLowerInvariant();

            // JSON is never valid media - always reject
            if (mediaType is "application/json")
                return true;

            // Small text/HTML/XML responses are likely error messages
            if (mediaType is "text/plain" or "text/html" or "application/xml")
            {
                if (content.Length < 1000)
                    return true;
            }
        }

        // Check for very small files that start with error indicators
        if (content.Length < 100)
        {
            try
            {
                var text = Encoding.UTF8.GetString(content);
                if (
                    text.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                    || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return true;
                }
            }
            catch
            {
                // Not valid UTF-8 text, so it's likely binary content
            }
        }

        return false;
    }
}
