using SmartX.Contracts.Attachments;
using SmartX.Contracts.Sensors;
using SmartX.Core.Models;
using SmartX.Core.Security;
using SmartX.Core.Telemetry;

namespace SmartX.Api.Services;

/// <summary>
/// Translates between the domain types and the published wire contract.
/// <para>
/// This mapping is written out by hand rather than delegated to a convention. The
/// contract assembly deliberately carries no reference to the domain library, so
/// the two shapes are free to diverge - and when they do, the compiler should point
/// at one obvious file rather than a reflection-based mapper failing at runtime.
/// It is also the single place where a domain enum becomes a wire string, which is
/// what stops an added enum member silently changing the published API.
/// </para>
/// </summary>
public static class ContractMapper
{
    /// <summary>Projects a sensor into its response shape.</summary>
    /// <param name="sensor">The registered sensor.</param>
    /// <param name="asOfUtc">Clock reading used to derive status and staleness.</param>
    /// <param name="attachmentCount">Files attached to this sensor profile.</param>
    /// <param name="latestReading">Most recent reading, already formatted, or null.</param>
    public static SensorResponse ToResponse(
        SensorRecord sensor,
        DateTimeOffset asOfUtc,
        int attachmentCount = 0,
        string? latestReading = null)
    {
        ArgumentNullException.ThrowIfNull(sensor);

        return new SensorResponse
        {
            Id = sensor.Id,
            MacAddress = sensor.MacAddress,
            DisplayName = sensor.DisplayName,
            Category = sensor.Category.ToString(),
            Unit = sensor.Unit.ToString(),
            Status = sensor.EvaluateStatus(asOfUtc).ToString(),
            Facility = sensor.Location.Facility,
            Zone = sensor.Location.Zone,
            SubZone = sensor.Location.SubZone,
            NodeId = sensor.Location.NodeId,
            QualifiedPath = sensor.Location.QualifiedPath,
            FirmwareVersion = sensor.FirmwareVersion,
            HeartbeatIntervalSeconds = sensor.HeartbeatIntervalSeconds,
            RegisteredAtUtc = sensor.RegisteredAtUtc,
            LastSeenUtc = sensor.LastSeenUtc,
            SecondsSinceLastSeen = sensor.LastSeenUtc is { } seen
                ? (asOfUtc - seen).TotalSeconds
                : null,
            PacketsReceived = sensor.PacketsReceived,
            AttachmentCount = attachmentCount,
            LatestReading = latestReading
        };
    }

    /// <summary>Builds a domain sensor from a validated registration request.</summary>
    /// <exception cref="FormatException">The MAC address is malformed.</exception>
    public static SensorRecord ToRecord(RegisterSensorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new SensorRecord
        {
            // Normalised once, here, so everything downstream can compare ordinally.
            MacAddress = MacAddress.Normalise(request.MacAddress),
            DisplayName = request.DisplayName.Trim(),
            Category = ParseCategory(request.Category),
            Unit = ParseUnit(request.Unit),
            Location = new DeploymentLocation(
                request.Facility.Trim(),
                request.Zone.Trim(),
                request.SubZone?.Trim() ?? string.Empty,
                request.NodeId.Trim()),
            FirmwareVersion = request.FirmwareVersion.Trim(),
            HeartbeatIntervalSeconds = request.HeartbeatIntervalSeconds
        };
    }

    /// <summary>Projects an attachment into its response shape.</summary>
    public static AttachmentResponse ToResponse(SensorAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);

        return new AttachmentResponse
        {
            Id = attachment.Id,
            SensorId = attachment.SensorId,
            FileName = attachment.OriginalFileName,
            ContentType = attachment.ContentType,
            SizeBytes = attachment.SizeBytes,
            SizeDisplay = FormatBytes(attachment.SizeBytes),
            Sha256 = attachment.Sha256,
            Encryption = AttachmentEncryption.Algorithm,
            UploadedAtUtc = attachment.UploadedAtUtc,
            IsImage = attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            DownloadUrl = $"/api/sensors/{attachment.SensorId}/attachments/{attachment.Id}/download"
        };
    }

    /// <summary>Parses a wire category, falling back to Environmental.</summary>
    public static SensorCategory ParseCategory(string? value)
        => Enum.TryParse<SensorCategory>(value, ignoreCase: true, out var parsed)
            ? parsed
            : SensorCategory.Environmental;

    /// <summary>Parses a wire unit, falling back to None.</summary>
    public static TelemetryUnit ParseUnit(string? value)
        => Enum.TryParse<TelemetryUnit>(value, ignoreCase: true, out var parsed)
            ? parsed
            : TelemetryUnit.None;

    /// <summary>Parses a wire deployment tier, falling back to Device.</summary>
    public static DeploymentNodeKind ParseNodeKind(string? value)
        => Enum.TryParse<DeploymentNodeKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : DeploymentNodeKind.Device;

    /// <summary>Renders a byte count for display, e.g. "1.4 MB".</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;

        while (size >= 1024d && unit < units.Length - 1)
        {
            size /= 1024d;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    /// <summary>Renders a reading with its unit symbol, e.g. "1450 W".</summary>
    public static string Format(SensorReading reading) => reading.ToString();
}
