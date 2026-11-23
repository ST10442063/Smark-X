using System.Security.Cryptography;
using SmartX.Core.Security;

namespace SmartX.Core.Tests.Security;

/// <summary>
/// Covers the chunked AES-GCM attachment format. Round-tripping is the easy half;
/// the tests that matter are the tamper cases - a flipped bit, a truncated file, a
/// reordered chunk or the wrong key must all refuse to decrypt rather than hand
/// back altered bytes.
/// </summary>
public class AttachmentEncryptionTests
{
    private const int SmallChunk = 16;

    private static readonly byte[] Key = AttachmentEncryption.GenerateKey();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(SmallChunk - 1)]
    [InlineData(SmallChunk)]
    [InlineData(SmallChunk * 3)]
    [InlineData(SmallChunk * 3 + 7)]
    public async Task EncryptThenDecrypt_ReturnsTheOriginalBytes(int length)
    {
        var plaintext = RandomNumberGenerator.GetBytes(length);

        var ciphertext = await EncryptAsync(plaintext, SmallChunk);
        var roundTripped = await DecryptAsync(ciphertext, Key);

        Assert.Equal(plaintext, roundTripped);
    }

    [Fact]
    public async Task ALargeFile_RoundTripsWithTheDefaultChunkSize()
    {
        var plaintext = RandomNumberGenerator.GetBytes(AttachmentEncryption.DefaultChunkSize * 4 + 1234);

        var ciphertext = await EncryptAsync(plaintext, AttachmentEncryption.DefaultChunkSize);

        Assert.Equal(plaintext, await DecryptAsync(ciphertext, Key));
    }

    [Fact]
    public async Task SynchronousReads_DecryptTheSameAsAsyncReads()
    {
        var plaintext = RandomNumberGenerator.GetBytes(SmallChunk * 5 + 3);
        var ciphertext = await EncryptAsync(plaintext, SmallChunk);

        using var decrypting = new AesGcmDecryptingStream(new MemoryStream(ciphertext), Key);
        using var output = new MemoryStream();
        decrypting.CopyTo(output, bufferSize: 5);

        Assert.Equal(plaintext, output.ToArray());
    }

    [Fact]
    public async Task TheCiphertext_DoesNotContainThePlaintext()
    {
        var plaintext = "ssid=HydroBay3;psk=very-secret-password;mqtt=10.0.0.4"u8.ToArray();

        var ciphertext = await EncryptAsync(plaintext, SmallChunk);

        Assert.Equal(-1, ciphertext.AsSpan().IndexOf("very-secret-password"u8));
    }

    [Fact]
    public async Task EncryptingTheSameFileTwice_ProducesDifferentCiphertext()
    {
        var plaintext = RandomNumberGenerator.GetBytes(100);

        var first = await EncryptAsync(plaintext, SmallChunk);
        var second = await EncryptAsync(plaintext, SmallChunk);

        // A fresh random nonce prefix per file, so identical uploads are unlinkable.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task AFlippedBit_IsRejected()
    {
        var ciphertext = await EncryptAsync(RandomNumberGenerator.GetBytes(SmallChunk * 2), SmallChunk);

        ciphertext[AttachmentEncryption.HeaderSize + AttachmentEncryption.ChunkHeaderSize + 3] ^= 0x01;

        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(ciphertext, Key));
    }

    [Fact]
    public async Task TheWrongKey_IsRejected()
    {
        var ciphertext = await EncryptAsync(RandomNumberGenerator.GetBytes(40), SmallChunk);

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => DecryptAsync(ciphertext, AttachmentEncryption.GenerateKey()));
    }

    [Fact]
    public async Task DroppingTheFinalChunk_IsDetectedAsTruncation()
    {
        var plaintext = RandomNumberGenerator.GetBytes(SmallChunk * 2 + 5);
        var ciphertext = await EncryptAsync(plaintext, SmallChunk);

        // Cut the file cleanly at a chunk boundary: every remaining chunk is intact
        // and passes its own tag, so only the missing final flag gives it away.
        var fullChunk = AttachmentEncryption.ChunkHeaderSize + SmallChunk + AttachmentEncryption.TagSize;
        var truncated = ciphertext[..(AttachmentEncryption.HeaderSize + fullChunk * 2)];

        var error = await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(truncated, Key));
        Assert.Contains("truncated", error.Message);
    }

    [Fact]
    public async Task SwappingTwoChunks_IsRejected()
    {
        var plaintext = RandomNumberGenerator.GetBytes(SmallChunk * 3);
        var ciphertext = await EncryptAsync(plaintext, SmallChunk);

        var fullChunk = AttachmentEncryption.ChunkHeaderSize + SmallChunk + AttachmentEncryption.TagSize;
        var first = ciphertext.AsSpan(AttachmentEncryption.HeaderSize, fullChunk).ToArray();
        var second = ciphertext.AsSpan(AttachmentEncryption.HeaderSize + fullChunk, fullChunk).ToArray();

        second.CopyTo(ciphertext, AttachmentEncryption.HeaderSize);
        first.CopyTo(ciphertext, AttachmentEncryption.HeaderSize + fullChunk);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(ciphertext, Key));
    }

    [Fact]
    public async Task BytesAppendedAfterTheFinalChunk_AreRejected()
    {
        var ciphertext = await EncryptAsync(RandomNumberGenerator.GetBytes(20), SmallChunk);

        byte[] padded = [.. ciphertext, 0x00];

        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(padded, Key));
    }

    [Fact]
    public async Task AnUnfinishedStream_CannotBeDecrypted()
    {
        using var destination = new MemoryStream();

        await using (var encrypting = new AesGcmEncryptingStream(destination, Key, SmallChunk, leaveOpen: true))
        {
            // Simulates an upload cancelled part-way: written, never finished.
            await encrypting.WriteAsync(RandomNumberGenerator.GetBytes(SmallChunk * 2 + 1));
        }

        await Assert.ThrowsAnyAsync<CryptographicException>(() => DecryptAsync(destination.ToArray(), Key));
    }

    [Fact]
    public async Task PlaintextFiles_AreNotMistakenForEncryptedOnes()
    {
        var error = await Assert.ThrowsAnyAsync<CryptographicException>(
            () => DecryptAsync("2026-09-14 12:00:01 boot ok, rssi -61 dBm"u8.ToArray(), Key));

        Assert.Contains("format", error.Message);
    }

    [Fact]
    public async Task TheLengthCounters_ReportPlaintextAndCiphertextSizes()
    {
        var plaintext = RandomNumberGenerator.GetBytes(SmallChunk * 2 + 3);
        using var destination = new MemoryStream();

        await using var encrypting = new AesGcmEncryptingStream(destination, Key, SmallChunk, leaveOpen: true);
        await encrypting.WriteAsync(plaintext);
        await encrypting.FinishAsync();

        Assert.Equal(plaintext.Length, encrypting.PlaintextLength);
        Assert.Equal(destination.Length, encrypting.CiphertextLength);
    }

    [Fact]
    public void AKeyOfTheWrongLength_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmEncryptingStream(new MemoryStream(), new byte[16]));
        Assert.Throws<ArgumentException>(() => new AesGcmDecryptingStream(new MemoryStream(), new byte[31]));
    }

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, int chunkSize)
    {
        using var destination = new MemoryStream();

        await using (var encrypting = new AesGcmEncryptingStream(destination, Key, chunkSize, leaveOpen: true))
        {
            // Written in uneven pieces, so chunk assembly is exercised across write calls.
            for (var offset = 0; offset < plaintext.Length; offset += 7)
            {
                await encrypting.WriteAsync(plaintext.AsMemory(offset, Math.Min(7, plaintext.Length - offset)));
            }

            await encrypting.FinishAsync();
        }

        return destination.ToArray();
    }

    private static async Task<byte[]> DecryptAsync(byte[] ciphertext, byte[] key)
    {
        await using var decrypting = new AesGcmDecryptingStream(new MemoryStream(ciphertext), key);
        using var output = new MemoryStream();
        await decrypting.CopyToAsync(output);
        return output.ToArray();
    }
}
