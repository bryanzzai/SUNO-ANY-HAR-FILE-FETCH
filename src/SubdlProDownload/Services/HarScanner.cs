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

    private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ".mp3", ["audio/mp4"] = ".m4a", ["audio/x-m4a"] = ".m4a",
        ["audio/aac"] = ".aac", ["audio/wav"] = ".wav", ["audio/x-wav"] = ".wav",
        ["audio/flac"] = ".flac", ["audio/ogg"] = ".ogg", ["audio/opus"] = ".opus"
    };

    public static async Task<HarScanResult> ScanAsync(string harPath, int maxDisplayedRows, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(harPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("log", out var log) ||
            !log.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("HAR root does not contain log.entries as an array.");

        var titles = FindClipTitles(entries);
        var rightsRequests = FindMangoRightsRequests(entries);
        var rows = new List<HarEntryRow>(Math.Min(maxDisplayedRows, entries.GetArrayLength()));
        var seenContentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var downloadableEntries = 0;
        var opaquePayloadEntries = 0;
        var parseProblems = 0;
        var entryIndex = 0;

        foreach (var entry in entries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryIndex++;
            try
            {
                var row = ParseEntry(entry, entryIndex, titles, rightsRequests);
                if (row is null || !seenContentIds.Add(row.ContentId)) continue;
                downloadableEntries++;
                if (row.IsOpaquePayload) opaquePayloadEntries++;
                if (rows.Count < maxDisplayedRows) rows.Add(row);
            }
            catch { parseProblems++; }
        }

        return new HarScanResult(entryIndex, downloadableEntries, rows.Count, opaquePayloadEntries, parseProblems, rows);
    }

    private static HarEntryRow? ParseEntry(
        JsonElement entry, int entryIndex, IReadOnlyDictionary<string, string> titles,
        IReadOnlyDictionary<string, HarRequestRecipe> rightsRequests)
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
        if (!mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && !AudioExtensions.Contains(extension)) return null;
        if (!CanReplayAsGet(method, url)) return null;

        var contentId = Path.GetFileNameWithoutExtension(rawFileName);
        if (string.IsNullOrWhiteSpace(contentId)) return null;
        var embeddedBody = HasEmbeddedText(response) ? DecodeEmbeddedBody(response) : null;
        var hasRights = rightsRequests.TryGetValue(contentId, out var rightsRequest);
        var payload = DescribePayload(embeddedBody, hasRights);
        var releaseName = titles.TryGetValue(contentId, out var title) && !string.IsNullOrWhiteSpace(title)
            ? title : GetReleaseName(entry, request, response, rawFileName, url);

        return new HarEntryRow
        {
            EntryIndex = entryIndex,
            ReleaseName = releaseName,
            RawFileName = rawFileName,
            OutputFileName = releaseName + ".wav",
            ContentId = contentId,
            Url = url,
            PayloadStatus = payload.Status,
            IsOpaquePayload = payload.IsOpaque,
            EmbeddedBody = embeddedBody,
            RequestHeaders = requestHeaders,
            RightsRequest = rightsRequest
        };
    }

    private static Dictionary<string, HarRequestRecipe> FindMangoRightsRequests(JsonElement entries)
    {
        var result = new Dictionary<string, HarRequestRecipe>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            var request = entry.TryGetProperty("request", out var item) ? item : default;
            var url = GetString(request, "url");
            if (!url.Contains("/api/mango/rights", StringComparison.OrdinalIgnoreCase)) continue;
            var body = request.TryGetProperty("postData", out var postData) ? GetString(postData, "text") : string.Empty;
            var contentId = ReadRightsContentId(body);
            if (string.IsNullOrWhiteSpace(contentId)) continue;
            result[contentId] = new HarRequestRecipe(GetString(request, "method"), url, ReadHeaders(request), body);
        }
        return result;
    }

    private static string ReadRightsContentId(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("content_params", out var parameters)
                ? GetString(parameters, "content_id") : string.Empty;
        }
        catch (JsonException) { return string.Empty; }
    }

    private static Dictionary<string, string> FindClipTitles(JsonElement entries)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            var response = entry.TryGetProperty("response", out var item) ? item : default;
            var mime = GetMimeType(response, ReadHeaders(response));
            if (!mime.Contains("json", StringComparison.OrdinalIgnoreCase) || !HasEmbeddedText(response)) continue;
            try
            {
                using var document = JsonDocument.Parse(DecodeEmbeddedBody(response));
                CollectClipTitles(document.RootElement, result);
            }
            catch (JsonException) { }
            catch (FormatException) { }
        }
        return result;
    }

    private static void CollectClipTitles(JsonElement node, Dictionary<string, string> titles)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) CollectClipTitles(item, titles);
            return;
        }
        if (node.ValueKind != JsonValueKind.Object) return;

        var title = GetString(node, "title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            var id = GetString(node, "id");
            if (LooksLikeClipId(id)) titles[id] = title;
            if (node.TryGetProperty("media_urls", out var mediaUrls) && mediaUrls.ValueKind == JsonValueKind.Array)
            {
                foreach (var media in mediaUrls.EnumerateArray())
                {
                    var mediaUrl = GetString(media, "url");
                    if (Uri.TryCreate(mediaUrl, UriKind.Absolute, out var uri))
                    {
                        var mediaId = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
                        if (LooksLikeClipId(mediaId)) titles[mediaId] = title;
                    }
                }
            }
        }
        foreach (var property in node.EnumerateObject()) CollectClipTitles(property.Value, titles);
    }

    private static bool LooksLikeClipId(string value) => Guid.TryParse(value, out _);

    private static PayloadAnalysis DescribePayload(byte[]? body, bool matchingRightsRecord)
    {
        if (body is null) return new PayloadAnalysis("Live source will be fetched after selection", false);
        if (IsIsoBaseMedia(body)) return new PayloadAnalysis("M4A container verified", false);
        return matchingRightsRecord
            ? new PayloadAnalysis("Encrypted source with matching rights request", true)
            : new PayloadAnalysis("Opaque source; no matching rights request", true);
    }

    private static bool IsIsoBaseMedia(ReadOnlySpan<byte> body) => body.Length >= 8 && body[4..].StartsWith("ftyp"u8);
    private sealed record PayloadAnalysis(string Status, bool IsOpaque);

    private static string GetReleaseName(JsonElement entry, JsonElement request, JsonElement response, string rawFileName, string url)
    {
        foreach (var value in new[] { GetString(entry, "comment"), GetString(response, "comment"), GetString(request, "comment") })
            if (!string.IsNullOrWhiteSpace(value)) return value;
        return !string.IsNullOrWhiteSpace(rawFileName) ? Path.GetFileNameWithoutExtension(rawFileName) : url;
    }

    private static Dictionary<string, string> ReadHeaders(JsonElement owner)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array) return result;
        foreach (var header in headers.EnumerateArray())
        {
            var name = GetString(header, "name");
            if (!string.IsNullOrWhiteSpace(name)) result[name] = GetString(header, "value");
        }
        return result;
    }

    private static string GetMimeType(JsonElement response, IReadOnlyDictionary<string, string> headers)
    {
        if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("content", out var content))
        {
            var mime = GetString(content, "mimeType");
            if (!string.IsNullOrWhiteSpace(mime)) return NormalizeMime(mime);
        }
        return headers.TryGetValue("Content-Type", out var contentType) ? NormalizeMime(contentType) : string.Empty;
    }

    private static string NormalizeMime(string value) => value.Split(';')[0].Trim();

    private static string GetRawFileName(string url, IReadOnlyDictionary<string, string> headers, string mime, int entryIndex)
    {
        var disposition = TryFileNameFromContentDisposition(headers);
        if (!string.IsNullOrWhiteSpace(disposition)) return EnsureExtension(disposition, mime);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segment = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            if (!string.IsNullOrWhiteSpace(segment)) return EnsureExtension(segment, mime);
        }
        return EnsureExtension($"entry-{entryIndex:00000}", mime);
    }

    private static string TryFileNameFromContentDisposition(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Content-Disposition", out var value) || string.IsNullOrWhiteSpace(value)) return string.Empty;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!part.StartsWith("filename", StringComparison.OrdinalIgnoreCase)) continue;
            var raw = part[(part.IndexOf('=') + 1)..].Trim().Trim('"');
            var quote = raw.IndexOf("''", StringComparison.Ordinal);
            return Uri.UnescapeDataString(quote >= 0 ? raw[(quote + 2)..] : raw);
        }
        return string.Empty;
    }

    private static string EnsureExtension(string name, string mime) => !string.IsNullOrWhiteSpace(Path.GetExtension(name))
        ? name : MimeExtensions.TryGetValue(mime, out var extension) ? name + extension : name;

    private static bool HasEmbeddedText(JsonElement response) => response.ValueKind == JsonValueKind.Object &&
        response.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object &&
        content.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String;

    private static byte[] DecodeEmbeddedBody(JsonElement response)
    {
        var content = response.GetProperty("content");
        var text = content.GetProperty("text").GetString() ?? string.Empty;
        return GetString(content, "encoding").Equals("base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(text) : Encoding.UTF8.GetBytes(text);
    }

    private static bool CanReplayAsGet(string method, string url) => method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string GetString(JsonElement owner, string propertyName)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(propertyName, out var value)) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : string.Empty;
    }
}
