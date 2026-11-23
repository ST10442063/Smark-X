using SmartX.Core.Telemetry;

namespace SmartX.Core.Tests.Telemetry;

/// <summary>
/// The converter is the piece that keeps the numeric projection off the boxing
/// path, so each supported payload type needs its own delegate proven.
/// </summary>
public class TelemetryValueConverterTests
{
    [Fact]
    public void SupportedPayloadTypes_ResolveADelegate()
    {
        Assert.True(TelemetryValueConverter<float>.IsSupported);
        Assert.True(TelemetryValueConverter<double>.IsSupported);
        Assert.True(TelemetryValueConverter<decimal>.IsSupported);
        Assert.True(TelemetryValueConverter<int>.IsSupported);
        Assert.True(TelemetryValueConverter<long>.IsSupported);
        Assert.True(TelemetryValueConverter<short>.IsSupported);
        Assert.True(TelemetryValueConverter<byte>.IsSupported);
        Assert.True(TelemetryValueConverter<bool>.IsSupported);
    }

    [Fact]
    public void EachSupportedType_ProjectsToTheExpectedDouble()
    {
        Assert.Equal(2.5d, TelemetryValueConverter<float>.ToDouble(2.5f), precision: 5);
        Assert.Equal(2.5d, TelemetryValueConverter<double>.ToDouble(2.5d));
        Assert.Equal(2.5d, TelemetryValueConverter<decimal>.ToDouble(2.5m));
        Assert.Equal(-7d, TelemetryValueConverter<int>.ToDouble(-7));
        Assert.Equal(9_000_000_000d, TelemetryValueConverter<long>.ToDouble(9_000_000_000L));
        Assert.Equal(-12d, TelemetryValueConverter<short>.ToDouble(-12));
        Assert.Equal(255d, TelemetryValueConverter<byte>.ToDouble(255));
        Assert.Equal(1d, TelemetryValueConverter<bool>.ToDouble(true));
        Assert.Equal(0d, TelemetryValueConverter<bool>.ToDouble(false));
    }

    [Fact]
    public void UnsupportedPayloadType_FailsLoudlyRatherThanSilently()
    {
        // Guid satisfies the struct constraint but is not something a sensor
        // publishes, so it must be rejected with a message that names the fix.
        Assert.False(TelemetryValueConverter<Guid>.IsSupported);

        var error = Assert.Throws<NotSupportedException>(
            () => TelemetryValueConverter<Guid>.ToDouble(Guid.NewGuid()));

        Assert.Contains("not supported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DelegateIsCachedPerClosedType()
    {
        // A static field on a generic type is separate per closed type, so the
        // same reference must come back on every access for a given T, and the
        // delegates for two different Ts must not be the same instance.
        Assert.Same(TelemetryValueConverter<float>.ToDouble, TelemetryValueConverter<float>.ToDouble);
        Assert.NotSame(TelemetryValueConverter<float>.ToDouble, TelemetryValueConverter<int>.ToDouble);
    }
}
