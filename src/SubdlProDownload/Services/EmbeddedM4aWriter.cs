using SunoHarFileDownload.Models;

namespace SunoHarFileDownload.Services;

public sealed class EmbeddedM4aWriter
{
    public async Task WriteAsync(
        IReadOnlyList<HarEntryRow> rows,
        IReadOnlyDictionary<HarEntryRow, string> finalNames,
        string outputFolder,
        Action<int, HarEntryRow, string> reportProgress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputFolder);

        for (var index = 0; index < rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            var destination = Path.Combine(outputFolder, finalNames[row]);
            if (File.Exists(destination))
                throw new IOException($"Destination already exists: {destination}");

            var body = row.EmbeddedBody ?? throw new InvalidDataException(
                $"'{row.ReleaseName}' has no embedded source body. Record a HAR with response bodies included.");
            reportProgress(index + 1, row, "Reconstructing embedded M4A");
            var m4a = IsM4a(body) ? body : DecryptEmbeddedSource(row, body);
            if (!IsM4a(m4a))
                throw new InvalidDataException($"'{row.ReleaseName}' did not decrypt to an M4A container.");

            var temporary = destination + ".part";
            try
            {
                await File.WriteAllBytesAsync(temporary, m4a, cancellationToken);
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    private static byte[] DecryptEmbeddedSource(HarEntryRow row, byte[] body)
    {
        var license = row.EmbeddedLicense ?? throw new InvalidDataException(
            $"'{row.ReleaseName}' has encrypted bytes but no embedded rights response.");
        var recipe = row.RightsRequest ?? throw new InvalidDataException(
            $"'{row.ReleaseName}' has encrypted bytes but no matching rights request.");
        if (!recipe.Headers.TryGetValue("Authorization", out var authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{row.ReleaseName}' has no bearer token in the embedded rights request.");

        return MangoDecryptor.Decrypt(row.ContentId, authorization["Bearer ".Length..].Trim(), license, body);
    }

    private static bool IsM4a(ReadOnlySpan<byte> bytes) => bytes.Length >= 8 && bytes[4..].StartsWith("ftyp"u8);
}
