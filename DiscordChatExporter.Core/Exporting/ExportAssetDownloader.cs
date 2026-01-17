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

        // Try to extract the file name from URL
        var pathAndFileName = Regex.Match(uri.AbsolutePath, @"/(.+)/([^?]*)");
        var path = pathAndFileName.Groups[1].Value;
        var fileName = pathAndFileName.Groups[2].Value;

        // If this isn't a Discord CDN URL, save the file to the `media/external` folder.
        if (!string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase))
        {
            return GetExternalFilePath(uri);
        }

        // If it is a Discord URL, we should have matches for both of these. <see cref="ImageCdn"/>
        // But if we encounter an unexpected Discord URL, just treat it like an external one.
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(fileName))
        {
            return GetExternalFilePath(uri);
        }

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var queryParamsString = FormatQueryParamsForFilename(uri);
        var fileExtension = Path.GetExtension(fileName);

        var queryParamsSuffix = string.IsNullOrEmpty(queryParamsString)
            ? ""
            : $"_{queryParamsString}";
        var fullFilename = $"{fileNameWithoutExtension}{queryParamsSuffix}{fileExtension}";

        return $"{path}/{Path.EscapeFileName(fullFilename)}";
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

    // Stringifies the query params to be included in a filename.
    // Returns a string like "size=256_spoiler=false"
    private static string FormatQueryParamsForFilename(Uri uri)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);
        return string.Join(
            "_",
            query
                .AllKeys.Where(key => !string.IsNullOrEmpty(key))
                .Select(key => string.IsNullOrEmpty(query[key]) ? key : $"{key}-{query[key]}")
        );
    }

    private static string GetExternalFilePath(Uri uri)
    {
        // Handle Discord external proxy URLs (e.g., images-ext-1.discordapp.net/external/...)
        // These URLs proxy external content and have the form:
        // https://images-ext-1.discordapp.net/external/{hash}/{protocol}/{original_url}
        // We strip the hash and protocol to get just the original URL path
        if (
            uri.Host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/external/", StringComparison.OrdinalIgnoreCase)
        )
        {
            // Extract path after /external/, skip the hash and protocol segments
            var externalPath = uri.AbsolutePath["/external/".Length..];
            var segments = externalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // Skip first segment (hash) and second segment (https/http)
            if (segments.Length > 2)
            {
                var originalUrl = string.Join("/", segments[2..]);
                return $"external/{SanitizePath(originalUrl)}";
            }

            // Fallback if URL structure is unexpected
            return $"external/{SanitizePath(externalPath)}";
        }

        // Handle twemoji URLs from jsdelivr CDN
        // These have the form: https://cdn.jsdelivr.net/gh/twitter/twemoji@latest/assets/svg/{emoji}.svg
        if (
            string.Equals(uri.Host, "cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/twemoji", StringComparison.OrdinalIgnoreCase)
        )
        {
            var fileName = System.IO.Path.GetFileName(uri.AbsolutePath);
            return $"twemoji/{Path.EscapeFileName(fileName)}";
        }

        // For other external URLs, use a condensed path structure
        return $"external/{SanitizePath(uri.Host + uri.AbsolutePath)}";
    }

    private static string SanitizePath(string path)
    {
        // Split the path into segments, sanitize each segment, and rejoin
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var sanitizedSegments = new List<string>();

        foreach (var segment in segments)
        {
            var sanitized = Path.EscapeFileName(segment);
            if (!string.IsNullOrWhiteSpace(sanitized))
                sanitizedSegments.Add(sanitized);
        }

        return string.Join("/", sanitizedSegments);
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
