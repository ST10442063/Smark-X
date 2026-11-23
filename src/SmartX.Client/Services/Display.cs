using System.Globalization;

namespace SmartX.Client.Services;

/// <summary>
/// Small formatting rules shared by several pages, kept in one place so the
/// registry, the workbench and the radar can never disagree about them.
/// </summary>
public static class Display
{
    /// <summary>
    /// Readings carried by one heartbeat strip. Mirrors the gateway's own strip
    /// length; it is a figure the radar quotes, not something the client controls.
    /// </summary>
    public const int StripLength = 40;

    /// <summary>
    /// The payload type a node of this category publishes. The pairing is what the
    /// gateway expects to receive from that class of device.
    /// </summary>
    public static string PayloadTypeFor(string? category) => category switch
    {
        "PowerConsumption" => "int",
        "Actuator" => "bool",
        "Connectivity" => "int",
        _ => "float"
    };

    /// <summary>The CLR type a payload is wrapped as, for the TelemetryPacket&lt;T&gt; hint.</summary>
    public static string ClrTypeFor(string payloadType) => payloadType switch
    {
        "int" => "Int32",
        "bool" => "Boolean",
        _ => "Single"
    };

    /// <summary>CSS modifier for a status or tier pill.</summary>
    public static string PillFor(string? state) => "pill-" + (state ?? "neutral").ToLowerInvariant();

    /// <summary>A count with its noun, singular or plural as it should read: "1 reading", "7 readings".</summary>
    /// <param name="count">How many.</param>
    /// <param name="singular">The noun for exactly one.</param>
    /// <param name="plural">The noun for any other count; defaults to <paramref name="singular"/> plus "s".</param>
    public static string Count(long count, string singular, string? plural = null)
        => $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural ?? singular + "s")}";

    /// <summary>A timestamp in the operator's local time zone.</summary>
    public static string Local(DateTimeOffset value, string format = "dd MMM HH:mm:ss")
        => value.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);

    /// <summary>A number formatted for CSS, which always wants a dot as the decimal separator.</summary>
    public static string Css(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a search term is a MAC address, in any of the forms firmware reports.
    /// Decides whether the registry search can use the gateway's binary search.
    /// </summary>
    public static bool LooksLikeMacAddress(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        var stripped = query
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            // Spaces too, to match what the gateway normaliser strips - otherwise a
            // pasted "A4 CF 12 9B 0E 77" would fall through to the linear scan.
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim();

        return stripped.Length == 12 && stripped.All(Uri.IsHexDigit);
    }
}
