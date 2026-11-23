using System.Globalization;
using SmartX.Core.Models;

namespace SmartX.Core.Devices;

/// <summary>
/// A device MAC address held as the 48-bit number it really is, for use as a
/// dictionary key.
/// <para>
/// <b>Why not just key on the string.</b> A string key works, but every lookup
/// hashes all seventeen characters and every equality check compares them again.
/// The device registry is hit once for every packet the mesh publishes, so the
/// key is parsed once, at the edge of the API, into a <see cref="ulong"/>. Hashing
/// and comparing are then single machine operations.
/// </para>
/// <para>
/// <b>Why the hash cannot collide inside one fleet.</b> A MAC is a 24-bit vendor
/// prefix (the OUI, e.g. <c>A4:CF:12</c> for Espressif) followed by a 24-bit device
/// suffix. A fleet of ESP32 boards shares one prefix, so only the suffix varies.
/// <see cref="GetHashCode"/> folds the top 16 bits onto the bottom 32 with an XOR.
/// With the prefix fixed, that is a constant XORed onto a value whose low 24 bits
/// <i>are</i> the suffix, which is a one-to-one mapping. So every device in a single
/// vendor block gets a distinct hash code, and a dictionary lookup makes exactly one
/// equality comparison, against the device it was looking for. The tests check this
/// over sixteen thousand addresses.
/// </para>
/// </summary>
public readonly struct MacKey : IEquatable<MacKey>, IComparable<MacKey>
{
    /// <summary>The largest value a 48-bit address can hold.</summary>
    public const ulong MaxValue = 0xFFFF_FFFF_FFFFUL;

    private MacKey(ulong value) => Value = value;

    /// <summary>The address as a 48-bit number, e.g. <c>0xA4CF129B0E77</c>.</summary>
    public ulong Value { get; }

    /// <summary>The 24-bit vendor prefix (OUI) that identifies the chip maker.</summary>
    public uint VendorPrefix => (uint)(Value >> 24);

    /// <summary>The 24-bit device suffix the vendor assigned to this board.</summary>
    public uint DeviceSuffix => (uint)(Value & 0xFF_FFFF);

    /// <summary>
    /// Parses an address in any form <see cref="MacAddress"/> accepts - colons,
    /// dashes, Cisco dots or bare hex.
    /// </summary>
    public static bool TryParse(string? candidate, out MacKey key)
    {
        if (!MacAddress.TryNormalise(candidate, out var normalised))
        {
            key = default;
            return false;
        }

        var value = 0UL;
        foreach (var character in normalised)
        {
            if (character == ':')
            {
                continue;
            }

            value = (value << 4) | (uint)HexValue(character);
        }

        key = new MacKey(value);
        return true;
    }

    /// <summary>Parses an address, or throws when it is malformed.</summary>
    /// <exception cref="FormatException">The address is not well-formed.</exception>
    public static MacKey Parse(string? candidate)
        => TryParse(candidate, out var key)
            ? key
            : throw new FormatException(
                $"'{candidate}' is not a valid MAC address. Expected the form {MacAddress.ExampleFormat}.");

    /// <summary>Builds a key from a raw 48-bit value.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit in 48 bits.</exception>
    public static MacKey FromValue(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxValue);
        return new MacKey(value);
    }

    /// <inheritdoc />
    public bool Equals(MacKey other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MacKey other && Equals(other);

    /// <summary>
    /// Folds the 48 bits into 32 by XORing the high 16 onto the low 32. Within one
    /// vendor prefix this is one-to-one - see the type remarks.
    /// </summary>
    public override int GetHashCode() => unchecked((int)((uint)Value ^ (uint)(Value >> 32)));

    /// <summary>
    /// Orders keys numerically. That is the same order as comparing the normalised
    /// upper-case strings ordinally, so a sorted list of keys reads the same as the
    /// Part 1 sorted sensor index.
    /// </summary>
    public int CompareTo(MacKey other) => Value.CompareTo(other.Value);

    /// <summary>The canonical upper-case colon form, e.g. <c>A4:CF:12:9B:0E:77</c>.</summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[17];
        for (var octet = 0; octet < 6; octet++)
        {
            var b = (byte)(Value >> (40 - (octet * 8)));
            b.TryFormat(buffer.Slice(octet * 3, 2), out _, "X2", CultureInfo.InvariantCulture);

            if (octet < 5)
            {
                buffer[(octet * 3) + 2] = ':';
            }
        }

        return new string(buffer);
    }

    /// <summary>Equality by address.</summary>
    public static bool operator ==(MacKey left, MacKey right) => left.Equals(right);

    /// <summary>Inequality by address.</summary>
    public static bool operator !=(MacKey left, MacKey right) => !left.Equals(right);

    /// <summary>Numeric ordering.</summary>
    public static bool operator <(MacKey left, MacKey right) => left.CompareTo(right) < 0;

    /// <summary>Numeric ordering.</summary>
    public static bool operator >(MacKey left, MacKey right) => left.CompareTo(right) > 0;

    /// <summary>Numeric ordering.</summary>
    public static bool operator <=(MacKey left, MacKey right) => left.CompareTo(right) <= 0;

    /// <summary>Numeric ordering.</summary>
    public static bool operator >=(MacKey left, MacKey right) => left.CompareTo(right) >= 0;

    private static int HexValue(char character) => character switch
    {
        >= '0' and <= '9' => character - '0',
        >= 'A' and <= 'F' => character - 'A' + 10,
        _ => throw new FormatException($"'{character}' is not a hexadecimal digit.")
    };
}
