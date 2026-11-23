namespace SmartX.Core.Models;

/// <summary>
/// Metadata for a configuration file, deployment photo or hardware log that an
/// operator has attached to a sensor profile.
/// <para>
/// The file bytes live on disk, encrypted with AES-256-GCM, under
/// <see cref="StoredFileName"/> (a generated GUID name). The operator's original file name is kept for display only and is
/// never used to build a path, which is what keeps a hostile name such as
/// "../../appsettings.json" from escaping the upload directory.
/// </para>
/// </summary>
public sealed class SensorAttachment
{
    /// <summary>Gateway-assigned attachment key.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Owning sensor.</summary>
    public required Guid SensorId { get; init; }

    /// <summary>Name as supplied by the uploader. Display only.</summary>
    public required string OriginalFileName { get; init; }

    /// <summary>GUID-based name the bytes are actually stored under on disk.</summary>
    public required string StoredFileName { get; init; }

    /// <summary>MIME type resolved from the extension allow-list.</summary>
    public required string ContentType { get; init; }

    /// <summary>Size of the original file in bytes, before encryption.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>SHA-256 of the original plaintext, so a re-upload of the same log is detectable.</summary>
    public required string Sha256 { get; init; }

    /// <summary>When the upload completed.</summary>
    public DateTimeOffset UploadedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
