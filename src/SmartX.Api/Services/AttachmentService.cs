using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SmartX.Api.Models;
using SmartX.Core.Models;
using SmartX.Core.Security;

namespace SmartX.Api.Services;

/// <summary>Why an upload was refused.</summary>
public enum UploadFailure
{
    /// <summary>Nothing was refused.</summary>
    None = 0,

    /// <summary>No file arrived, or it was empty.</summary>
    Empty = 1,

    /// <summary>The extension is not on the allow-list.</summary>
    DisallowedType = 2,

    /// <summary>The file is larger than the configured cap.</summary>
    TooLarge = 3,

    /// <summary>The bytes do not match what the extension claims.</summary>
    ContentMismatch = 4
}

/// <summary>Outcome of an upload attempt.</summary>
/// <param name="Failure">Why it was refused, or None.</param>
/// <param name="Attachment">The stored attachment, on success.</param>
/// <param name="Message">Explanation suitable for returning to the caller.</param>
public readonly record struct UploadOutcome(
    UploadFailure Failure,
    SensorAttachment? Attachment,
    string Message)
{
    /// <summary>True when the file was stored.</summary>
    public bool Succeeded => Failure == UploadFailure.None;
}

/// <summary>
/// Stores device configuration files, deployment photos and hardware logs against
/// a sensor profile.
/// <para>
/// <b>Streaming, not buffering.</b> The bytes are copied straight from the request
/// stream to a file stream, so a 10 MB log never exists as a 10 MB array on the
/// heap. Reading it into a byte array first would put every concurrent upload onto
/// the large object heap at once, and on a gateway running alongside the ingestion
/// pipeline that is the difference between a slow request and a gen-2 collection.
/// The SHA-256 is computed by wrapping the same stream, so hashing costs no extra
/// pass over the data.
/// </para>
/// <para>
/// <b>Encrypted at rest.</b> Device configuration dumps routinely carry Wi-Fi
/// passphrases and broker credentials, so the bytes are never written to disk in
/// the clear. The copy passes through an <see cref="AesGcmEncryptingStream"/> that
/// seals the file in 64 KB AES-256-GCM chunks as it streams, which keeps the
/// memory profile flat and also authenticates the contents: a stored file that has
/// been altered on disk fails to decrypt instead of being served back. The hash is
/// taken over the plaintext, before encryption, so it still identifies the file the
/// operator actually uploaded.
/// </para>
/// <para>
/// <b>The uploader never names a path.</b> Bytes are written under a generated GUID
/// name; the operator's file name is kept in metadata for display only. That is
/// what makes a file called "../../appsettings.json" harmless - it is never
/// concatenated into a path at all.
/// </para>
/// </summary>
public sealed class AttachmentService
{
    private readonly GatewayOptions _options;
    private readonly AttachmentKeyProvider _keys;
    private readonly ILogger<AttachmentService> _logger;
    private readonly string _uploadRoot;

    /// <summary>Creates the service and ensures the upload directory exists.</summary>
    public AttachmentService(
        IOptions<GatewayOptions> options,
        AttachmentKeyProvider keys,
        IWebHostEnvironment environment,
        ILogger<AttachmentService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(environment);

        _options = options.Value;
        _keys = keys;
        _logger = logger;

        _uploadRoot = Path.IsPathRooted(_options.UploadRoot)
            ? _options.UploadRoot
            : Path.Combine(environment.ContentRootPath, _options.UploadRoot);

        Directory.CreateDirectory(_uploadRoot);
    }

    /// <summary>Accepted extensions, including the leading dot.</summary>
    public IReadOnlyList<string> AllowedExtensions => _options.AllowedUploadExtensions;

    /// <summary>Largest accepted file, in bytes.</summary>
    public long MaxUploadBytes => _options.MaxUploadBytes;

    /// <summary>
    /// Streams an uploaded file to disk and returns its metadata.
    /// </summary>
    /// <param name="sensorId">Sensor to attach it to.</param>
    /// <param name="file">The uploaded file.</param>
    /// <param name="cancellationToken">Cancels a long upload if the client disconnects.</param>
    public async Task<UploadOutcome> StoreAsync(
        Guid sensorId,
        IFormFile? file,
        CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return new UploadOutcome(UploadFailure.Empty, null, "No file was supplied, or the file was empty.");
        }

        if (file.Length > _options.MaxUploadBytes)
        {
            return new UploadOutcome(
                UploadFailure.TooLarge,
                null,
                $"That file is {ContractMapper.FormatBytes(file.Length)}. " +
                $"The limit is {ContractMapper.FormatBytes(_options.MaxUploadBytes)}.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

        if (string.IsNullOrEmpty(extension) || !_options.AllowedUploadExtensions.Contains(extension))
        {
            return new UploadOutcome(
                UploadFailure.DisallowedType,
                null,
                $"'{extension}' files are not accepted. Allowed types: " +
                string.Join(", ", _options.AllowedUploadExtensions) + ".");
        }

        await using var source = file.OpenReadStream();

        // Sniff the leading bytes before writing anything. An executable renamed to
        // .png would otherwise be stored and later served back with an image
        // content type, which is the shape of a stored-XSS delivery.
        var header = new byte[8];
        var headerLength = await source.ReadAsync(header, cancellationToken);

        if (!HeaderMatchesExtension(header.AsSpan(0, headerLength), extension))
        {
            _logger.LogWarning(
                "Rejected upload '{FileName}' for sensor {SensorId}: content does not match its extension.",
                file.FileName, sensorId);

            return new UploadOutcome(
                UploadFailure.ContentMismatch,
                null,
                $"The contents of '{file.FileName}' do not look like a {extension} file.");
        }

        // The name on disk is generated; the uploader's name never touches a path.
        // The .enc suffix is deliberate: nothing in the upload directory is servable
        // as-is, and the name says so to anyone looking at the volume.
        var storedName = $"{Guid.NewGuid():N}.enc";
        var destinationPath = Path.Combine(_uploadRoot, storedName);

        long bytesWritten;
        long bytesOnDisk;
        string hash;

        try
        {
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            // The pipeline, outermost first:
            //   request stream -> SHA-256 (plaintext) -> AES-256-GCM -> file
            // One pass over the bytes produces the hash and the ciphertext together.
            await using var encrypting = new AesGcmEncryptingStream(destination, _keys.Key, leaveOpen: true);
            using var sha = SHA256.Create();
            await using var hashing = new CryptoStream(encrypting, sha, CryptoStreamMode.Write, leaveOpen: true);

            // The header bytes were already consumed by the sniff, so replay them
            // before copying the remainder of the stream.
            await hashing.WriteAsync(header.AsMemory(0, headerLength), cancellationToken);
            await source.CopyToAsync(hashing, cancellationToken);
            await hashing.FlushFinalBlockAsync(cancellationToken);
            await encrypting.FinishAsync(cancellationToken);

            bytesWritten = encrypting.PlaintextLength;
            bytesOnDisk = encrypting.CiphertextLength;
            hash = Convert.ToHexString(sha.Hash ?? []).ToLowerInvariant();
        }
        catch
        {
            // A client that disconnects mid-upload leaves a partial ciphertext with no
            // final chunk. It could never decrypt, so it is removed rather than left to
            // accumulate in the volume.
            TryDelete(destinationPath);
            throw;
        }

        var attachment = new SensorAttachment
        {
            SensorId = sensorId,
            OriginalFileName = Path.GetFileName(file.FileName),
            StoredFileName = storedName,
            ContentType = ResolveContentType(extension),
            SizeBytes = bytesWritten,
            Sha256 = hash
        };

        _logger.LogInformation(
            "Stored {Size} attachment for sensor {SensorId} as {StoredName} ({Algorithm}, {DiskSize} on disk).",
            ContractMapper.FormatBytes(bytesWritten), sensorId, storedName,
            AttachmentEncryption.Algorithm, ContractMapper.FormatBytes(bytesOnDisk));

        return new UploadOutcome(UploadFailure.None, attachment, "File attached.");
    }

    /// <summary>
    /// Opens a stored attachment for reading, decrypting it as it is read.
    /// <para>
    /// Only one 64 KB chunk of plaintext exists in memory at a time. If the file on
    /// disk has been tampered with, the read throws when it reaches the altered
    /// chunk rather than returning modified bytes.
    /// </para>
    /// </summary>
    /// <returns>The decrypted stream, or null when the bytes are missing from disk.</returns>
    public Stream? OpenRead(SensorAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        // Re-derived from the stored GUID name, never from anything the uploader
        // supplied, so this cannot be steered out of the upload directory.
        var path = Path.Combine(_uploadRoot, attachment.StoredFileName);

        if (!File.Exists(path))
        {
            return null;
        }

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return new AesGcmDecryptingStream(file, _keys.Key);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException error)
        {
            _logger.LogWarning(error, "Could not remove the partial upload {Path}.", path);
        }
    }

    /// <summary>
    /// Confirms the leading bytes are consistent with the claimed extension.
    /// Text formats have no signature to check, so they pass; the binary formats
    /// this gateway accepts all do.
    /// </summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool HeaderMatchesExtension(ReadOnlySpan<byte> header, string extension) => extension switch
    {
        ".png" => header.Length >= 8 && header[..8].SequenceEqual(PngSignature),
        ".jpg" or ".jpeg" => header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
        _ => true
    };

    private static string ResolveContentType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".yaml" or ".yml" => "application/yaml",
        _ => "text/plain"
    };
}
