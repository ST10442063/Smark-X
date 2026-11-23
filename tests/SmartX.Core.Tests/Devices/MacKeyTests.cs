using SmartX.Core.Devices;

namespace SmartX.Core.Tests.Devices;

/// <summary>
/// Covers the MAC dictionary key. The claim worth proving is the one the registry
/// relies on: inside one vendor block, no two devices share a hash code.
/// </summary>
public class MacKeyTests
{
    [Theory]
    [InlineData("A4:CF:12:9B:0E:77")]
    [InlineData("a4-cf-12-9b-0e-77")]
    [InlineData("a4cf.129b.0e77")]
    [InlineData("A4CF129B0E77")]
    public void EveryAcceptedForm_ParsesToTheSameKey(string form)
    {
        var key = MacKey.Parse(form);

        Assert.Equal(0xA4CF129B0E77UL, key.Value);
        Assert.Equal("A4:CF:12:9B:0E:77", key.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A4:CF:12:9B:0E")]
    [InlineData("G4:CF:12:9B:0E:77")]
    public void MalformedAddresses_AreRefused(string? candidate)
    {
        Assert.False(MacKey.TryParse(candidate, out _));
        Assert.Throws<FormatException>(() => MacKey.Parse(candidate));
    }

    [Fact]
    public void ValuesWiderThan48Bits_AreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MacKey.FromValue(MacKey.MaxValue + 1));
        Assert.Equal("FF:FF:FF:FF:FF:FF", MacKey.FromValue(MacKey.MaxValue).ToString());
    }

    [Fact]
    public void ThePrefixAndSuffix_SplitTheAddress()
    {
        var key = MacKey.Parse("A4:CF:12:9B:0E:77");

        Assert.Equal(0xA4CF12U, key.VendorPrefix);
        Assert.Equal(0x9B0E77U, key.DeviceSuffix);
    }

    [Fact]
    public void WithinOneVendorBlock_NoTwoDevicesShareAHashCode()
    {
        // Sixteen thousand ESP32 boards from one OUI, with suffixes spread across
        // the whole 24-bit range rather than just counting up from zero.
        var hashes = new HashSet<int>();
        const int fleet = 16_384;

        for (var i = 0; i < fleet; i++)
        {
            var suffix = (ulong)((i * 1021L) % 0x100_0000);
            var key = MacKey.FromValue((0xA4CF12UL << 24) | suffix);
            Assert.True(hashes.Add(key.GetHashCode()), $"Hash collision at device {i}.");
        }

        Assert.Equal(fleet, hashes.Count);
    }

    [Fact]
    public void Ordering_MatchesTheOrdinalOrderOfTheCanonicalStrings()
    {
        var random = new Random(7312);
        var keys = Enumerable.Range(0, 500)
            .Select(_ => MacKey.FromValue((ulong)random.NextInt64(0, (long)MacKey.MaxValue)))
            .ToList();

        var byKey = keys.OrderBy(key => key).Select(key => key.ToString());
        var byString = keys.Select(key => key.ToString()).OrderBy(text => text, StringComparer.Ordinal);

        Assert.Equal(byString, byKey);
    }

    [Fact]
    public void AsADictionaryKey_DifferentlyWrittenAddressesFindTheSameEntry()
    {
        var map = new Dictionary<MacKey, string> { [MacKey.Parse("A4:CF:12:9B:0E:77")] = "Water Pump A" };

        Assert.Equal("Water Pump A", map[MacKey.Parse("a4cf.129b.0e77")]);
        Assert.True(MacKey.Parse("a4-cf-12-9b-0e-77") == MacKey.Parse("A4CF129B0E77"));
    }
}
