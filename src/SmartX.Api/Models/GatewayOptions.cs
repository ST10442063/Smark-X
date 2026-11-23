namespace SmartX.Api.Models;

/// <summary>
/// Tuning for the ingestion gateway, bound from the <c>Gateway</c> section of
/// appsettings.json so the pipeline can be re-tuned without a rebuild.
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Gateway";

    /// <summary>
    /// Live packets held per sensor in the ingestion ring buffer. Once full, the
    /// oldest is overwritten - by then it has already been flushed to the
    /// historical store, so nothing is lost.
    /// </summary>
    public int BufferCapacity { get; set; } = 512;

    /// <summary>
    /// How many recent readings the anomaly detector measures a new reading
    /// against. Too short and normal variation looks like a spike; too long and a
    /// slow drift never registers.
    /// </summary>
    public int RollingWindow { get; set; } = 40;

    /// <summary>Standard deviations from the rolling mean before a reading is a warning.</summary>
    public double WarningZScore { get; set; } = 2.0;

    /// <summary>Standard deviations before a reading is critical.</summary>
    public double CriticalZScore { get; set; } = 3.0;

    /// <summary>Readings the detector needs before it will judge anything as anomalous.</summary>
    public int MinimumSamplesBeforeScoring { get; set; } = 8;

    /// <summary>
    /// How far a reading must sit from the rolling mean, as a fraction of that mean,
    /// before it can be flagged - on top of crossing the z-score threshold.
    /// <para>
    /// This exists because real telemetry trends. When a signal is midway through a
    /// slow swing, consecutive readings sit close together, the rolling standard
    /// deviation collapses, and perfectly ordinary movement scores three or four
    /// sigma. Without this second test the dashboard fills with alerts for a probe
    /// that moved 2.6% on a 39% baseline, and the one reading that genuinely
    /// mattered is buried among them.
    /// </para>
    /// <para>
    /// A reading now has to be both statistically surprising and materially
    /// different before an operator is asked to look at it.
    /// </para>
    /// </summary>
    public double MinimumRelativeDeparture { get; set; } = 0.20;

    /// <summary>Directory the uploaded attachment bytes are written to.</summary>
    public string UploadRoot { get; set; } = "App_Data/attachments";

    /// <summary>Largest accepted upload, in bytes.</summary>
    public long MaxUploadBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Base64 encoding of the 256-bit AES key attachments are encrypted with.
    /// <para>
    /// Supply this from an environment variable or a secret store in any real
    /// deployment (<c>Gateway__AttachmentEncryptionKey</c>). When it is absent the
    /// gateway falls back to <see cref="EncryptionKeyPath"/>, generating a key there
    /// on first run, so a development machine works with no setup at all.
    /// </para>
    /// </summary>
    public string? AttachmentEncryptionKey { get; set; }

    /// <summary>
    /// File the attachment key is read from, or generated into, when
    /// <see cref="AttachmentEncryptionKey"/> is not configured. Deliberately a
    /// different directory from <see cref="UploadRoot"/>: a key stored beside the
    /// ciphertext protects nothing if that one directory is copied.
    /// </summary>
    public string EncryptionKeyPath { get; set; } = "App_Data/keys/attachments.key";

    /// <summary>The extensions used when configuration supplies none.</summary>
    public static readonly string[] DefaultUploadExtensions =
        [".log", ".txt", ".json", ".yaml", ".yml", ".csv", ".cfg", ".ini", ".png", ".jpg", ".jpeg"];

    /// <summary>
    /// Accepted upload extensions, lower-case and including the dot.
    /// <para>
    /// This starts empty rather than pre-populated, because the configuration
    /// binder *merges* bound values into a collection that already has contents
    /// instead of replacing them - which silently doubled this list when the same
    /// extensions appeared in both places. The fallback is applied after binding
    /// instead, in the PostConfigure step in Program.cs, so what is configured is
    /// exactly what applies.
    /// </para>
    /// </summary>
    public string[] AllowedUploadExtensions { get; set; } = [];

    /// <summary>Whether to seed mock sensors and telemetry at startup.</summary>
    public bool SeedOnStartup { get; set; } = true;

    /// <summary>Mock sensors to create when seeding.</summary>
    public int SeedSensorCount { get; set; } = 14;

    /// <summary>
    /// Readings to synthesise per mock sensor, one a minute ending at startup. 1,440
    /// is a full day, so a clock window such as 14:00-16:00 always has history in it
    /// for the topology page's AVL range queries, whenever the gateway was started.
    /// </summary>
    public int SeedReadingsPerSensor { get; set; } = 1440;

    /// <summary>
    /// How much raw history each sensor's AVL tree keeps behind its newest reading.
    /// Older readings are trimmed from the left edge of the tree as new ones arrive.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Range(1, 24 * 30)]
    public int HistoryRetentionHours { get; set; } = 48;
}
