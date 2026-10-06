using System.IO;
using System.Net.Http;
using SunoHarFileDownload.Models;

namespace SunoHarFileDownload.Services;

public sealed class MediaDownloader : IDisposable
{
    private static readonly HashSet<string> SkippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Connection",
        "Accept-Encoding",
        "Transfer-Encoding",
        "Range",
        "If-Range"
    };

    private readonly HttpClient _client = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public async Task DownloadAsync(
        HarEntryRow row,
        string outputFolder,
        string finalFileName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolder);

        var destination = Path.Combine(outputFolder, finalFileName);
        if (File.Exists(destination))
            throw new IOException($"Destination already exists: {destination}");

        var temporary = destination + ".part";
        if (File.Exists(temporary))
            File.Delete(temporary);

        try
        {
            if (row.EmbeddedBody is not null)
            {
                await File.WriteAllBytesAsync(temporary, row.EmbeddedBody, cancellationToken);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, row.Url);

                foreach (var pair in row.RequestHeaders)
                {
                    if (SkippedRequestHeaders.Contains(pair.Key) ||
                        pair.Key.StartsWith(":", StringComparison.Ordinal))
                        continue;

                    request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                }

                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 128,
                    useAsync: true);

                await source.CopyToAsync(target, cancellationToken);
            }

            File.Move(temporary, destination);
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            throw;
        }
    }

    public void Dispose() => _client.Dispose();
}
