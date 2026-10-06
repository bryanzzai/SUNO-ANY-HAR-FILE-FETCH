using System.Security.Cryptography;
using System.Text;
using SunoHarFileDownload.Models;

namespace SunoHarFileDownload.Services;

/// <summary>
/// Mirrors Suno's browser playback path: the rights response is unwrapped with
/// AES-GCM using the current bearer token, then the media stream is decrypted
/// with AES-CTR. The decrypted bytes remain the original M4A/Opus source.
/// </summary>
public static class MangoDecryptor
{
    private const int GcmNonceBytes = 12;
    private const int GcmTagBytes = 16;
    private const int AesBlockBytes = 16;

    public static byte[] Decrypt(string contentId, string bearerToken, MangoLicense license, byte[] encryptedMedia)
    {
        if (string.IsNullOrWhiteSpace(contentId))
            throw new ArgumentException("A Suno content id is required.", nameof(contentId));
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new InvalidOperationException("The HAR does not contain a bearer token for the rights request.");

        var wrappingKey = SHA256.HashData(Encoding.UTF8.GetBytes(bearerToken));
        var streamKey = Unwrap(Convert.FromBase64String(license.Key), wrappingKey, contentId);
        var counter = Unwrap(Convert.FromBase64String(license.Iv), wrappingKey, contentId);

        if (counter.Length < AesBlockBytes)
            throw new CryptographicException("The rights response produced an invalid AES-CTR counter.");

        return DecryptCtr(encryptedMedia, streamKey, counter);
    }

    private static byte[] Unwrap(byte[] wrapped, byte[] wrappingKey, string contentId)
    {
        if (wrapped.Length <= GcmNonceBytes + GcmTagBytes)
            throw new CryptographicException("The rights response contains an incomplete wrapped value.");

        var cipherLength = wrapped.Length - GcmNonceBytes - GcmTagBytes;
        var clear = new byte[cipherLength];
        var nonce = wrapped.AsSpan(0, GcmNonceBytes);
        var cipher = wrapped.AsSpan(GcmNonceBytes, cipherLength);
        var tag = wrapped.AsSpan(GcmNonceBytes + cipherLength, GcmTagBytes);

        using var gcm = new AesGcm(wrappingKey, GcmTagBytes);
        gcm.Decrypt(nonce, cipher, tag, clear, Encoding.UTF8.GetBytes(contentId));
        return clear;
    }

    private static byte[] DecryptCtr(byte[] encrypted, byte[] key, byte[] initialCounter)
    {
        var output = new byte[encrypted.Length];
        var counter = initialCounter[..AesBlockBytes].ToArray();
        var keystream = new byte[AesBlockBytes];

        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        using var encryptor = aes.CreateEncryptor();

        for (var offset = 0; offset < encrypted.Length; offset += AesBlockBytes)
        {
            encryptor.TransformBlock(counter, 0, AesBlockBytes, keystream, 0);
            var count = Math.Min(AesBlockBytes, encrypted.Length - offset);
            for (var index = 0; index < count; index++)
                output[offset + index] = (byte)(encrypted[offset + index] ^ keystream[index]);

            IncrementCounter(counter);
        }

        return output;
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (var index = counter.Length - 1; index >= 0; index--)
        {
            if (++counter[index] != 0)
                return;
        }
    }
}
