using Microsoft.Extensions.Options;
using SmartX.Api.Models;
using SmartX.Core.Security;

namespace SmartX.Api.Services;

/// <summary>
/// Supplies the AES-256 key that sensor attachments are encrypted with.
/// <para>
/// <b>Where the key comes from, in order.</b> A configured
/// <c>Gateway:AttachmentEncryptionKey</c> wins, because that is how a real
/// deployment injects a secret - an environment variable or a vault reference,
/// never a file in the repository. Without one, the key is read from
/// <c>Gateway:EncryptionKeyPath</c>, and generated there on first run. That fallback
/// keeps "clone, build, run" working with no setup, while still never putting a
/// fixed key in source control where anyone with the repo could decrypt uploads.
/// </para>
/// <para>
/// The key is resolved once at startup and held for the life of the process, so
/// uploads never touch the key file on the hot path.
/// </para>
/// </summary>
public sealed class AttachmentKeyProvider
{
    private readonly byte[] _key;

    /// <summary>Resolves the key from configuration or the key file.</summary>
    public AttachmentKeyProvider(
        IOptions<GatewayOptions> options,
        IWebHostEnvironment environment,
        ILogger<AttachmentKeyProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var gateway = options.Value;

        if (!string.IsNullOrWhiteSpace(gateway.AttachmentEncryptionKey))
        {
            // Already proven decodable by the options validation in Program.cs.
            TryDecodeKey(gateway.AttachmentEncryptionKey, out _key);
            Source = "configuration";
            logger.LogInformation("Attachment encryption key loaded from configuration.");
            return;
        }

        var path = Path.IsPathRooted(gateway.EncryptionKeyPath)
            ? gateway.EncryptionKeyPath
            : Path.Combine(environment.ContentRootPath, gateway.EncryptionKeyPath);

        _key = LoadOrCreate(path, logger);
        Source = path;
    }

    /// <summary>Where the key came from, for diagnostics. Never the key itself.</summary>
    public string Source { get; }

    /// <summary>The 256-bit key.</summary>
    public ReadOnlySpan<byte> Key => _key;

    /// <summary>Decodes a base64 key, succeeding only for exactly 256 bits.</summary>
    public static bool TryDecodeKey(string? encoded, out byte[] key)
    {
        key = [];

        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        var buffer = new byte[AttachmentEncryption.KeySizeBytes + 3];
        if (!Convert.TryFromBase64String(encoded.Trim(), buffer, out var written)
            || written != AttachmentEncryption.KeySizeBytes)
        {
            return false;
        }

        key = buffer[..written];
        return true;
    }

    private static byte[] LoadOrCreate(string path, ILogger logger)
    {
        if (File.Exists(path))
        {
            if (!TryDecodeKey(File.ReadAllText(path), out var existing))
            {
                // Refusing is the only safe answer. Quietly generating a replacement
                // would make every attachment already stored undecryptable.
                throw new InvalidOperationException(
                    $"The attachment key file '{path}' does not contain a valid " +
                    $"{AttachmentEncryption.KeySizeBytes}-byte base64 key.");
            }

            logger.LogInformation("Attachment encryption key loaded from {KeyPath}.", path);
            return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var key = AttachmentEncryption.GenerateKey();

        // CreateNew, so two instances starting together cannot both write a key and
        // leave one of them encrypting with a key that was then overwritten.
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(Convert.ToBase64String(key));
        }

        if (!OperatingSystem.IsWindows())
        {
            // Owner read/write only - the same permissions an SSH private key gets.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogWarning(
            "Generated a new attachment encryption key at {KeyPath}. Back it up: attachments " +
            "cannot be recovered without it. In production, supply Gateway:AttachmentEncryptionKey instead.",
            path);

        return key;
    }
}
