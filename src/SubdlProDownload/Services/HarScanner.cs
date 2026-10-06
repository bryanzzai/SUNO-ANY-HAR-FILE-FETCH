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

    public static async Task<HarScanResult> ScanAsync(string harPath, int maxDisplayedRows, CancellationToken cancellationToken)
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
        var mediaCandidates = 0;
        var recoverableCandidates = 0;
        var parseProblems = 0;
        var entryIndex = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryIndex++;
            var materialize = entryIndex <= maxDisplayedRows;

            try
            {
                var row = ParseEntry(entry, entryIndex, materialize);

                if (row.IsMediaCandidate) mediaCandidates++;
                if (row.CanDownload) recoverableCandidates++;
                if (materialize) rows.Add(row);
            }
            catch (Exception ex)
            {
                parseProblems++;

                if (materialize)
                {
                    rows.Add(new HarEntryRow
                    {
                        EntryIndex = entryIndex,
                        Method = "?",
                        StatusCode = 0,
                        Classification = "ERROR",
                        MimeType = string.Empty,
                        RawFileName = $"entry-{entryIndex:00000}",
                        Url = TryGetRawUrl(entry),
                        SizeText = string.Empty,
                        RecoveryMode = "Unavailable",
                        Diagnostic = $"{ex.GetType().Name}: {ex.Message}",
                        IsMediaCandidate = false,
                        CanDownload = false
                    });
                }
            }
        }

        return new HarScanResult(
            entryIndex,
            rows.Count,
            mediaCandidates,
            recoverableCandidates,
            parseProblems,
            rows);
    }

    private static HarEntryRow ParseEntry(JsonElement entry, int entryIndex, bool materialize)
    {
        var request = entry.TryGetProperty("request", out var requestElement) ? requestElement : default;
        var response = entry.TryGetProperty("response", out var responseElement) ? responseElement : default;

        var method = GetString(request, "method");
        var url = GetString(request, "url");
        var status = GetInt32(response, "status");

        var requestHeaders = ReadHeaders(request);
        var responseHeaders = ReadHeaders(response);

        var mime = GetMimeType(response, responseHeaders);
        var rawName = GetRawFileName(url, responseHeaders, mime, entryIndex);
        var extension = Path.GetExtension(rawName);
        var classification = Classify(mime, extension);
        var isMedia = classification is "Audio" or "Video";

        byte[]? embeddedBody = null;
        var hasEmbeddedText = HasEmbeddedText(response);

        if (materialize && hasEmbeddedText)
            embeddedBody = DecodeEmbeddedBody(response);

        var canReplay = CanReplayAsGet(method, url);
        var canDownload = isMedia && (hasEmbeddedText || canReplay);
        var recoveryMode = hasEmbeddedText
            ? "Embedded"
            : canReplay
                ? "Replay GET"
                : "Unavailable";

        var size = GetContentSize(response);
        var diagnostic = BuildDiagnostic(mime, extension, hasEmbeddedText, canReplay, status, classification);

        return new HarEntryRow
        {
            EntryIndex = entryIndex,
            Method = string.IsNullOrWhiteSpace(method) ? "?" : method,
            StatusCode = status,
            Classification = classification,
            MimeType = mime,
            RawFileName = rawName,
            Url = url,
            SizeText = FormatBytes(size),
            RecoveryMode = recoveryMode,
            Diagnostic = diagnostic,
            IsMediaCandidate = isMedia,
            CanDownload = canDownload,
            EmbeddedBody = embeddedBody,
            RequestHeaders = requestHeaders
        };
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
            if (string.IsNullOrWhiteSpace(name)) continue;
            result[name] = value;
        }

        return result;
    }

    private static string GetMimeType(JsonElement response, IReadOnlyDictionary<string, string> responseHeaders)
    {
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("content", out var content))
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

    private static string Classify(string mime, string extension)
    {
        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || AudioExtensions.Contains(extension))
            return "Audio";

        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || VideoExtensions.Contains(extension))
            return "Video";

        return "Other";
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

        var fallback = $"entry-{entryIndex:00000}";
        return EnsureExtension(fallback, mime);
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

        if (MimeExtensions.TryGetValue(mime, out var extension))
            return fileName + extension;

        return fileName;
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

        if (encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
            return Convert.FromBase64String(text);

        return Encoding.UTF8.GetBytes(text);
    }

    private static bool CanReplayAsGet(string method, string url)
    {
        if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return false;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private static long? GetContentSize(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object) return null;

        if (response.TryGetProperty("content", out var content))
        {
            var size = GetInt64(content, "size");
            if (size is >= 0) return size;
        }

        var bodySize = GetInt64(response, "bodySize");
        return bodySize is >= 0 ? bodySize : null;
    }

    private static string BuildDiagnostic(
        string mime,
        string extension,
        bool embedded,
        bool replay,
        int status,
        string classification)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(mime)) parts.Add($"mime={mime}");
        if (!string.IsNullOrWhiteSpace(extension)) parts.Add($"ext={extension}");
        if (status != 0) parts.Add($"http={status}");
        if (embedded) parts.Add("embedded-body");
        if (replay) parts.Add("replayable-get");
        if (classification == "Other") parts.Add("no-audio-video-signal");

        return string.Join(" | ", parts);
    }

    private static string FormatBytes(long? size)
    {
        if (size is null) return string.Empty;

        double value = size.Value;
        string[] units = ["B", "KB", "MB", "GB"];
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private static string GetString(JsonElement owner, string propertyName)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(propertyName, out var value))
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

    private static int GetInt32(JsonElement owner, string propertyName)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;

        return 0;
    }

    private static long? GetInt64(JsonElement owner, string propertyName)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;

        return null;
    }

    private static string TryGetRawUrl(JsonElement entry)
    {
        try
        {
            if (entry.TryGetProperty("request", out var request))
                return GetString(request, "url");
        }
        catch
        {
            // This is only emergency diagnostic recovery.
        }

        return string.Empty;
    }
}
