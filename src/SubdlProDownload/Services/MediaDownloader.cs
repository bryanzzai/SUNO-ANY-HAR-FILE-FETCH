using System.Net.Http;
using System.Text;
using System.Text.Json;
using SunoHarFileDownload.Models;

namespace SunoHarFileDownload.Services;

public sealed class MediaDownloader : IDisposable
{
    private static readonly HashSet<string> SkippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Connection", "Accept-Encoding", "Transfer-Encoding", "Range", "If-Range", "Content-Type"
    };

    private readonly HttpClient _client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    }) { Timeout = TimeSpan.FromMinutes(10) };

    public async Task<byte[]> FetchLiveMediaAsync(HarEntryRow row, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(HttpMethod.Get, row.Url, row.RequestHeaders, null);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<MangoLicense> FetchCurrentLicenseAsync(HarEntryRow row, CancellationToken cancellationToken)
    {
        var recipe = row.RightsRequest ?? throw new InvalidOperationException(
            "No matching rights request was found. Capture a fresh HAR while playing this song.");
        using var request = BuildRequest(new HttpMethod(recipe.Method), recipe.Url, recipe.Headers, recipe.Body);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var license = await JsonSerializer.DeserializeAsync<MangoLicense>(stream, cancellationToken: cancellationToken);
        return license ?? throw new InvalidOperationException("The rights service returned no usable key material.");
    }

    public static string GetBearerToken(HarEntryRow row)
    {
        var recipe = row.RightsRequest ?? throw new InvalidOperationException("No matching rights request was found.");
        if (!recipe.Headers.TryGetValue("Authorization", out var authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The HAR does not contain a bearer token for the rights request.");
        return authorization["Bearer ".Length..].Trim();
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, IReadOnlyDictionary<string, string> headers, string? body)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        foreach (var pair in headers)
        {
            if (SkippedRequestHeaders.Contains(pair.Key) || pair.Key.StartsWith(':')) continue;
            var value = pair.Key.Equals("browser-token", StringComparison.OrdinalIgnoreCase)
                ? MakeBrowserToken() : pair.Value;
            request.Headers.TryAddWithoutValidation(pair.Key, value);
        }
        return request;
    }

    private static string MakeBrowserToken()
    {
        var json = $"{{\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{{\"token\":\"{encoded}\"}}";
    }

    public void Dispose() => _client.Dispose();
}
