using System.IO;
using System.Text;
using System.Text.Json;
using SunoHarFileDownload.Models;

namespace SunoHarFileDownload.Services;

public static class HarScanner
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus", ".aiff", ".aif", ".wma"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".mpeg", ".mpg", ".wmv", ".ts"
    };

    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ".mp3",
        ["audio/mp4"] = ".m4a",
        ["audio/x-m4a"] = ".m4a",
        ["audio/aac"] = ".aac",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/flac"] = ".flac",
        ["audio/ogg"] = ".ogg",
        ["audio/opus"] = ".opus",
        ["video/mp4"] = ".mp4",
        ["video/quicktime"] = ".mov",
        ["video/webm"] = ".webm",
        ["video/x-matroska"] = ".mkv",
        ["video/mpeg"] = ".mpeg"
    };

    public static async Task<HarScanResult> ScanAsync(
        string harPath,
        int maxDisplayedRows,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(harPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("log", out var log) ||
            !log.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("HAR root does not contain log.entries as an array.");
        }

        var rows = new List<HarEntryRow>(Math.Min(maxDisplayedRows, entries.GetArrayLength()));
        var downloadableEntries = 0;
        var parseProblems = 0;
        var entryIndex = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryIndex++;

            try
            {
                var parsed = ParseEntry(entry, entryIndex, rows.Count < maxDisplayedRows);
                if (!parsed.Downloadable) continue;

                downloadableEntries++;
                if (parsed.Row is not null)
                    rows.Add(parsed.Row);
            }
            catch
            {
                parseProblems++;
            }
        }

        return new HarScanResult(
            entryIndex,
            downloadableEntries,
            rows.Count,
            parseProblems,
            rows);
    }

    private static (bool Downloadable, HarEntryRow? Row) ParseEntry(
        JsonElement entry,
        int entryIndex,
        bool materialize)
    {
        var request = entry.TryGetProperty("request", out var requestElement) ? requestElement : default;
        var response = entry.TryGetProperty("response", out var responseElement) ? responseElement : default;

        var method = GetString(request, "method");
        var url = GetString(request, "url");
        var requestHeaders = ReadHeaders(request);
        var responseHeaders = ReadHeaders(response);
        var mime = GetMimeType(response, responseHeaders);
        var rawFileName = GetRawFileName(url, responseHeaders, mime, entryIndex);
        var extension = Path.GetExtension(rawFileName);

        var isAudio = mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || AudioExtensions.Contains(extension);
        var isVideo = mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || VideoExtensions.Contains(extension);
        if (!isAudio && !isVideo)
            return (false, null);

        var hasEmbeddedBody = HasEmbeddedText(response);
        var canReplay = CanReplayAsGet(method, url);
        if (!hasEmbeddedBody && !canReplay)
            return (false, null);

        if (!materialize)
            return (true, null);

        var releaseName = GetReleaseName(entry, request, response, rawFileName, url);
        var embeddedBody = hasEmbeddedBody ? DecodeEmbeddedBody(response) : null;

        return (true, new HarEntryRow
        {
            EntryIndex = entryIndex,
            ReleaseName = releaseName,
            RawFileName = rawFileName,
            Url = url,
            EmbeddedBody = embeddedBody,
            RequestHeaders = requestHeaders
        });
    }

    private static string GetReleaseName(
        JsonElement entry,
        JsonElement request,
        JsonElement response,
        string rawFileName,
        string url)
    {
        var entryComment = GetString(entry, "comment");
        if (!string.IsNullOrWhiteSpace(entryComment)) return entryComment;

        var responseComment = GetString(response, "comment");
        if (!string.IsNullOrWhiteSpace(responseComment)) return responseComment;

        var requestComment = GetString(request, "comment");
        if (!string.IsNullOrWhiteSpace(requestComment)) return requestComment;

        if (!string.IsNullOrWhiteSpace(rawFileName)) return rawFileName;
        if (!string.IsNullOrWhiteSpace(url)) return url;

        return $"entry-{entryIndexFallback(entry)}";
    }

    private static string entryIndexFallback(JsonElement entry)
    {
        var started = GetString(entry, "startedDateTime");
        return string.IsNullOrWhiteSpace(started) ? "unknown" : started;
    }

    private static Dictionary<string, string> ReadHeaders(JsonElement owner)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty("headers", out var headers) ||
            headers.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var header in headers.EnumerateArray())
        {
            var name = GetString(header, "name");
            var value = GetString(header, "value");
            if (!string.IsNullOrWhiteSpace(name))
                result[name] = value;
        }

        return result;
    }

    private static string GetMimeType(JsonElement response, IReadOnlyDictionary<string, string> responseHeaders)
    {
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("content", out var content))
        {
            var fromContent = GetString(content, "mimeType");
            if (!string.IsNullOrWhiteSpace(fromContent))
                return NormalizeMime(fromContent);
        }

        if (responseHeaders.TryGetValue("Content-Type", out var contentType))
            return NormalizeMime(contentType);

        return string.Empty;
    }

    private static string NormalizeMime(string value)
    {
        var semicolon = value.IndexOf(';');
        return (semicolon >= 0 ? value[..semicolon] : value).Trim();
    }

    private static string GetRawFileName(
        string url,
        IReadOnlyDictionary<string, string> responseHeaders,
        string mime,
        int entryIndex)
    {
        var fromDisposition = TryFileNameFromContentDisposition(responseHeaders);
        if (!string.IsNullOrWhiteSpace(fromDisposition))
            return EnsureExtension(fromDisposition, mime);

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segment = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(segment))
                return EnsureExtension(segment, mime);
        }

        return EnsureExtension($"entry-{entryIndex:00000}", mime);
    }

    private static string TryFileNameFromContentDisposition(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Content-Disposition", out var value) || string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string? plain = null;

        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("filename*=", StringComparison.OrdinalIgnoreCase))
            {
                var raw = part["filename*=".Length..].Trim().Trim('"');
                var quote = raw.IndexOf("''", StringComparison.Ordinal);
                if (quote >= 0) raw = raw[(quote + 2)..];
                return Uri.UnescapeDataString(raw);
            }

            if (part.StartsWith("filename=", StringComparison.OrdinalIgnoreCase))
                plain = part["filename=".Length..].Trim().Trim('"');
        }

        return plain ?? string.Empty;
    }

    private static string EnsureExtension(string fileName, string mime)
    {
        if (!string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
            return fileName;

        return MimeExtensions.TryGetValue(mime, out var extension)
            ? fileName + extension
            : fileName;
    }

    private static bool HasEmbeddedText(JsonElement response)
    {
        return response.ValueKind == JsonValueKind.Object &&
               response.TryGetProperty("content", out var content) &&
               content.ValueKind == JsonValueKind.Object &&
               content.TryGetProperty("text", out var text) &&
               text.ValueKind == JsonValueKind.String;
    }

    private static byte[] DecodeEmbeddedBody(JsonElement response)
    {
        var content = response.GetProperty("content");
        var text = content.GetProperty("text").GetString() ?? string.Empty;
        var encoding = GetString(content, "encoding");

        return encoding.Equals("base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(text)
            : Encoding.UTF8.GetBytes(text);
    }

    private static bool CanReplayAsGet(string method, string url)
    {
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return false;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetString(JsonElement owner, string propertyName)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(propertyName, out var value))
            return string.Empty;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }
}
