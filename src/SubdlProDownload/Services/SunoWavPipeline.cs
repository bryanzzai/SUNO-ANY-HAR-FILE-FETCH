using System.IO.Compression;
using SunoHarFileDownload.Models;

namespace SunoHarFileDownload.Services;

public sealed class SunoWavPipeline
{
    private readonly FfmpegRunner _ffmpeg = new();

    public async Task<string> CreateZipAsync(
        IReadOnlyList<HarEntryRow> rows,
        IReadOnlyDictionary<HarEntryRow, string> finalNames,
        string outputFolder,
        Action<int, HarEntryRow, string> reportProgress,
        CancellationToken cancellationToken)
    {
        var stagingFolder = Path.Combine(outputFolder, ".suno-har-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingFolder);

        try
        {
            using var downloader = new MediaDownloader();
            for (var index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = rows[index];
                reportProgress(index + 1, row, "Fetching complete source");
                var media = await downloader.FetchLiveMediaAsync(row, cancellationToken);

                if (!IsM4a(media))
                {
                    reportProgress(index + 1, row, "Requesting rights and decoding source");
                    var license = await downloader.FetchCurrentLicenseAsync(row, cancellationToken);
                    media = MangoDecryptor.Decrypt(row.ContentId, MediaDownloader.GetBearerToken(row), license, media);
                }

                if (!IsM4a(media))
                    throw new InvalidDataException($"The live source for '{row.ReleaseName}' did not decode to an M4A container.");

                var m4aPath = Path.Combine(stagingFolder, row.ContentId + ".m4a");
                var wavPath = Path.Combine(stagingFolder, finalNames[row]);
                try
                {
                    await File.WriteAllBytesAsync(m4aPath, media, cancellationToken);
                    reportProgress(index + 1, row, "Decoding M4A/Opus to PCM WAV");
                    await _ffmpeg.WritePcmWavAsync(m4aPath, wavPath, cancellationToken);
                }
                finally
                {
                    if (File.Exists(m4aPath)) File.Delete(m4aPath);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            var zipPath = NextZipPath(outputFolder);
            ZipFile.CreateFromDirectory(stagingFolder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            return zipPath;
        }
        finally
        {
            if (Directory.Exists(stagingFolder)) Directory.Delete(stagingFolder, recursive: true);
        }
    }

    private static bool IsM4a(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[4..].StartsWith("ftyp"u8);

    private static string NextZipPath(string outputFolder)
    {
        var baseName = "Suno WAV";
        var candidate = Path.Combine(outputFolder, baseName + ".zip");
        for (var number = 2; File.Exists(candidate); number++)
            candidate = Path.Combine(outputFolder, $"{baseName} ({number}).zip");
        return candidate;
    }
}
