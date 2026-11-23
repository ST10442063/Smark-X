namespace SmartX.Contracts.Attachments;

/// <summary>A file attached to a sensor profile, as the API reports it.</summary>
public sealed class AttachmentResponse
{
    /// <summary>Gateway-assigned attachment key.</summary>
    public Guid Id { get; set; }

    /// <summary>Owning sensor.</summary>
    public Guid SensorId { get; set; }

    /// <summary>
    /// Name as supplied by the uploader. Display only - the bytes are stored under a
    /// generated name, so this never reaches the file system.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Resolved MIME type.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Size of the original file in bytes, before encryption.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Size rendered for display, e.g. "1.4 MB".</summary>
    public string SizeDisplay { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the original file, taken before encryption, so a duplicate upload is
    /// detectable and an operator can verify a download against it.
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>How the bytes are protected at rest, e.g. "AES-256-GCM".</summary>
    public string Encryption { get; set; } = string.Empty;

    /// <summary>When the upload completed.</summary>
    public DateTimeOffset UploadedAtUtc { get; set; }

    /// <summary>True when the file is an image and can be previewed inline.</summary>
    public bool IsImage { get; set; }

    /// <summary>Relative API path the bytes can be fetched from.</summary>
    public string DownloadUrl { get; set; } = string.Empty;
}

/// <summary>The limits the upload endpoint enforces, so the client can state them up front.</summary>
public sealed class AttachmentPolicyResponse
{
    /// <summary>Largest accepted file, in bytes.</summary>
    public long MaxSizeBytes { get; set; }

    /// <summary>Largest accepted file, rendered for display.</summary>
    public string MaxSizeDisplay { get; set; } = string.Empty;

    /// <summary>Accepted file extensions, including the leading dot.</summary>
    public List<string> AllowedExtensions { get; set; } = [];
}
