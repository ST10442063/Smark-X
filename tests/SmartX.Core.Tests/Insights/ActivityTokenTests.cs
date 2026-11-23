using SmartX.Core.Alerts;
using SmartX.Core.Devices;
using SmartX.Core.Insights;

namespace SmartX.Core.Tests.Insights;

/// <summary>Covers the event vocabulary the miner learns over.</summary>
public class ActivityTokenTests
{
    private static readonly MacKey Pump = MacKey.Parse("A4:CF:12:01:00:01");

    [Theory]
    [InlineData("Pump A", "pump a")]
    [InlineData("  pump    A  ", "pump a")]
    [InlineData("PUMP\tA", "pump a")]
    public void Searches_AreNormalised(string typed, string expected)
        => Assert.Equal($"search:{expected}", ActivityToken.Search(typed));

    [Fact]
    public void ABlankSearch_ProducesNoToken()
        => Assert.Null(ActivityToken.Search("   "));

    [Fact]
    public void ALongSearch_IsCut()
        => Assert.Equal(ActivityToken.MaxSearchLength, ActivityToken.NormaliseSearch(new string('x', 500)).Length);

    [Fact]
    public void EveryKind_RoundTrips()
    {
        Assert.True(ActivityToken.TryParse(ActivityToken.Alert(AlertCode.CriticalLow, Pump), out var alert));
        Assert.Equal((ActivityKind.Alert, Pump, AlertCode.CriticalLow), (alert.Kind, alert.Mac!.Value, alert.Code!.Value));

        Assert.True(ActivityToken.TryParse(ActivityToken.Disconnect(Pump), out var down));
        Assert.Equal(ActivityKind.Disconnect, down.Kind);

        Assert.True(ActivityToken.TryParse(ActivityToken.Inspect(Pump), out var inspect));
        Assert.Equal(Pump, inspect.Mac);

        Assert.True(ActivityToken.TryParse(ActivityToken.Command(true, Pump), out var command));
        Assert.Equal((ActivityKind.Command, true), (command.Kind, command.State!.Value));

        Assert.True(ActivityToken.TryParse(ActivityToken.Search("Pump A"), out var search));
        Assert.Equal("pump a", search.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("command:sideways:A4:CF:12:01:00:01")]
    [InlineData("alert:NotACode:A4:CF:12:01:00:01")]
    [InlineData("inspect:not-a-mac")]
    public void ForeignTokens_AreRejected(string? token)
        => Assert.False(ActivityToken.TryParse(token, out _));
}
