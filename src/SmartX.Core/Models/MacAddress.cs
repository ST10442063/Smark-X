using System.Text.RegularExpressions;

namespace SmartX.Core.Models;

/// <summary>
/// Parsing and normalisation for device MAC addresses.
/// <para>
/// Nodes report their address in whichever form their firmware vendor chose:
/// <c>A4:CF:12:9B:0E:77</c>, <c>a4-cf-12-9b-0e-77</c>, <c>a4cf.129b.0e77</c> or
/// bare hex. Normalising once at registration - to upper-case, colon-separated -
/// means everything downstream can compare addresses with a plain ordinal string
/// comparison, which is what lets the sorted index binary-search them.
/// </para>
/// </summary>
public static partial class MacAddress
{
    [GeneratedRegex("^[0-9A-Fa-f]{12}$")]
    private static partial Regex BareHexPattern();

    /// <summary>The canonical form used everywhere in the system.</summary>
    public const string ExampleFormat = "A4:CF:12:9B:0E:77";

    /// <summary>
    /// Normalises <paramref name="candidate"/> to upper-case colon form.
    /// </summary>
    /// <param name="candidate">Address in any of the accepted forms.</param>
    /// <param name="normalised">The canonical address, when parsing succeeded.</param>
    /// <returns>True when the input was a well-formed 48-bit MAC address.</returns>
    public static bool TryNormalise(string? candidate, out string normalised)
    {
        normalised = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        // Strip every accepted separator, leaving bare hex to validate.
        var hex = candidate
            .Trim()
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (!BareHexPattern().IsMatch(hex))
        {
            return false;
        }

        hex = hex.ToUpperInvariant();

        normalised = string.Join(':',
            Enumerable.Range(0, 6).Select(octet => hex.Substring(octet * 2, 2)));

        return true;
    }

    /// <summary>True when <paramref name="candidate"/> is a well-formed MAC address.</summary>
    public static bool IsValid(string? candidate) => TryNormalise(candidate, out _);

    /// <summary>
    /// Normalises or throws. Used where the value has already been validated and a
    /// failure would mean a bug rather than bad input.
    /// </summary>
    /// <exception cref="FormatException">The address is not well-formed.</exception>
    public static string Normalise(string? candidate)
        => TryNormalise(candidate, out var normalised)
            ? normalised
            : throw new FormatException(
                $"'{candidate}' is not a valid MAC address. Expected the form {ExampleFormat}.");

    /// <summary>
    /// The vendor prefix (OUI) - the first three octets, which identify the chip
    /// manufacturer. Useful for grouping an ESP32 fleet by silicon vendor.
    /// </summary>
    public static string VendorPrefix(string normalisedMac)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalisedMac);
        return normalisedMac.Length >= 8 ? normalisedMac[..8] : normalisedMac;
    }
}
