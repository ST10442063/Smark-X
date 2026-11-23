using SmartX.Core.Models;
using SmartX.Core.Validation;

namespace SmartX.Core.Tests.Validation;

/// <summary>
/// Covers the recursive descent: that a well-formed profile resolves, that each
/// structural rule is caught, and - most importantly - that the two guards stop
/// a malformed profile from taking the stack down with it.
/// </summary>
public class RecursiveDeploymentValidatorTests
{
    private static DeploymentNode Node(
        string id,
        string name,
        DeploymentNodeKind kind,
        string? mac = null,
        int? heartbeat = null,
        DeploymentNode[]? children = null)
        => new()
        {
            Id = id,
            Name = name,
            Kind = kind,
            MacAddress = mac,
            HeartbeatIntervalSeconds = heartbeat,
            Children = children is null ? [] : [.. children]
        };

    /// <summary>Facility A -&gt; Zone 1 -&gt; Sub-Zone B -&gt; NODE-07, the worked example from the brief.</summary>
    private static DeploymentNode WellFormedProfile() =>
        Node("fac-a", "Facility A", DeploymentNodeKind.Facility, heartbeat: 60, children:
            [Node("zone-1", "Zone 1", DeploymentNodeKind.Zone,
                children: [Node("sub-b", "Sub-Zone B", DeploymentNodeKind.SubZone, heartbeat: 15,
                    children: [Node("node-07", "NODE-07", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77")])])]);

    [Fact]
    public void WellFormedProfile_Validates()
    {
        var result = RecursiveDeploymentValidator.Validate(WellFormedProfile());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(4, result.NodesVisited);
        Assert.Equal(3, result.MaxDepthReached);
    }

    [Fact]
    public void DeviceResolvesItsFullPath()
    {
        var result = RecursiveDeploymentValidator.Validate(WellFormedProfile());

        var device = Assert.Single(result.Devices);
        Assert.Equal("Facility A / Zone 1 / Sub-Zone B / NODE-07", device.QualifiedPath);
        Assert.Equal("A4:CF:12:9B:0E:77", device.MacAddress);
        Assert.Equal(3, device.Depth);
    }

    [Fact]
    public void HeartbeatIsInheritedFromTheNearestAncestorThatDeclaresOne()
    {
        // Facility declares 60, Sub-Zone B overrides to 15, the device declares
        // nothing - so the device should resolve to the sub-zone's 15, not the
        // facility's 60 and not the 30-second global default.
        var result = RecursiveDeploymentValidator.Validate(WellFormedProfile());

        Assert.Equal(15, Assert.Single(result.Devices).EffectiveHeartbeatSeconds);
    }

    [Fact]
    public void HeartbeatFallsBackToTheDefaultWhenNoTierDeclaresOne()
    {
        var profile = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children: [Node("node-1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77")]);

        var result = RecursiveDeploymentValidator.Validate(profile);

        Assert.Equal(
            RecursiveDeploymentValidator.DefaultHeartbeatSeconds,
            Assert.Single(result.Devices).EffectiveHeartbeatSeconds);
    }

    // -----------------------------------------------------------------
    // The guards - the reason this validator cannot take the stack down
    // -----------------------------------------------------------------

    [Fact]
    public void ExcessiveDepth_IsReportedRatherThanOverflowingTheStack()
    {
        // Build a chain far deeper than MaxDepth. Without the guard this is a
        // StackOverflowException, which cannot even be caught.
        var leaf = Node("device", "NODE-DEEP", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77");
        var current = leaf;
        for (var i = 0; i < RecursiveDeploymentValidator.MaxDepth + 20; i++)
        {
            current = Node($"sub-{i}", $"Sub-Zone {i}", DeploymentNodeKind.SubZone, children: [current]);
        }

        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility, children: [current]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "MAX_DEPTH");
        Assert.DoesNotContain(result.Errors, issue => issue.Code == "TIER_ORDER");
        Assert.True(result.MaxDepthReached <= RecursiveDeploymentValidator.MaxDepth);
    }

    [Fact]
    public void SelfReferencingProfile_IsCutOffRatherThanRecursingForever()
    {
        var zone = Node("zone-1", "Zone 1", DeploymentNodeKind.Zone);
        var subZone = Node("sub-b", "Sub-Zone B", DeploymentNodeKind.SubZone);

        zone.Children.Add(subZone);
        subZone.Children.Add(zone); // the cycle

        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility, children: [zone]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "CYCLE");
    }

    [Fact]
    public void ANodeObjectReusedAsASibling_IsNotMistakenForACycle()
    {
        // The same object appearing in two branches is legitimate - it is only a
        // cycle when a vertex is its own ancestor. The guard tracks the current
        // path, not every node ever seen, so this must pass.
        var sharedDevice = Node("node-07", "NODE-07", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77");

        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children:
            [
                Node("zone-1", "Zone 1", DeploymentNodeKind.Zone, children: [sharedDevice]),
                Node("zone-2", "Zone 2", DeploymentNodeKind.Zone, children: [sharedDevice])
            ]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.DoesNotContain(result.Errors, issue => issue.Code == "CYCLE");
    }

    // -----------------------------------------------------------------
    // Structural rules
    // -----------------------------------------------------------------

    [Fact]
    public void AFacilityNestedInsideAZone_BreaksTierOrder()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children: [Node("zone-1", "Zone 1", DeploymentNodeKind.Zone,
                children: [Node("fac-b", "Facility B", DeploymentNodeKind.Facility,
                    children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77")])])]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, issue => issue.Code == "TIER_ORDER");
    }

    [Fact]
    public void SubZonesMayNestInsideSubZones()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children: [Node("zone-1", "Zone 1", DeploymentNodeKind.Zone,
                children: [Node("row-3", "Row 3", DeploymentNodeKind.SubZone,
                    children: [Node("rack-b", "Rack B", DeploymentNodeKind.SubZone,
                        children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77")])])])]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.True(result.IsValid);
        Assert.Equal(4, Assert.Single(result.Devices).Depth);
    }

    [Theory]
    [InlineData(DeploymentNodeKind.Zone, DeploymentNodeKind.Zone)]
    [InlineData(DeploymentNodeKind.SubZone, DeploymentNodeKind.Zone)]
    public void TiersOtherThanSubZone_MayNotRepeatOrClimb(DeploymentNodeKind parent, DeploymentNodeKind child)
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children: [Node("p", "Parent", parent,
                children: [Node("c", "Child", child,
                    children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:78")])])]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.Contains(result.Errors, issue => issue.Code == "TIER_ORDER");
    }

    [Fact]
    public void ARootThatIsNotAFacility_IsRejected()
    {
        var root = Node("zone-1", "Zone 1", DeploymentNodeKind.Zone,
            children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77")]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.Contains(result.Errors, issue => issue.Code == "ROOT_TIER");
    }

    [Fact]
    public void ADeviceWithoutAMac_IsRejected()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children: [Node("n1", "NODE-01", DeploymentNodeKind.Device)]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.Contains(result.Errors, issue => issue.Code == "DEVICE_NO_MAC");
    }

    [Fact]
    public void ADeviceWithAMalformedMac_IsRejected()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "not-a-mac")]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.Contains(result.Errors, issue => issue.Code == "DEVICE_BAD_MAC");
    }

    [Fact]
    public void AZoneDeclaringAMac_IsRejected()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children: [Node("zone-1", "Zone 1", DeploymentNodeKind.Zone, mac: "A4:CF:12:9B:0E:77",
                children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:78")])]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.Contains(result.Errors, issue => issue.Code == "NON_DEVICE_MAC");
    }

    [Fact]
    public void ADeviceWithChildren_IsRejectedBecauseADeviceMustBeALeaf()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77",
                children: [Node("n2", "NODE-02", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:78")])]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.Contains(result.Errors, issue => issue.Code == "DEVICE_HAS_CHILDREN");
    }

    [Fact]
    public void DuplicateIdentifiers_AreRejected()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children:
            [
                Node("dup", "Zone 1", DeploymentNodeKind.Zone,
                    children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77")]),
                Node("dup", "Zone 2", DeploymentNodeKind.Zone,
                    children: [Node("n2", "NODE-02", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:78")])
            ]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.Contains(result.Errors, issue => issue.Code == "DUPLICATE_ID");
    }

    [Fact]
    public void AnOutOfRangeHeartbeat_IsRejected()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility, heartbeat: 99_999,
            children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77")]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.Contains(result.Errors, issue => issue.Code == "HEARTBEAT_RANGE");
    }

    [Fact]
    public void AnEmptyZone_WarnsWithoutBlockingDeployment()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children:
            [
                Node("zone-1", "Zone 1", DeploymentNodeKind.Zone),
                Node("zone-2", "Zone 2", DeploymentNodeKind.Zone,
                    children: [Node("n1", "NODE-01", DeploymentNodeKind.Device, mac: "A4:CF:12:9B:0E:77")])
            ]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, issue => issue.Code == "EMPTY_CONTAINER");
    }

    [Fact]
    public void AProfileWithNoDevices_Warns()
    {
        var root = Node("fac-a", "Facility A", DeploymentNodeKind.Facility,
            children: [Node("zone-1", "Zone 1", DeploymentNodeKind.Zone)]);

        var result = RecursiveDeploymentValidator.Validate(root);

        Assert.Contains(result.Warnings, issue => issue.Code == "NO_DEVICES");
    }

    // -----------------------------------------------------------------
    // The other recursive walks
    // -----------------------------------------------------------------

    [Fact]
    public void BuildQualifiedPath_AssemblesThePathAsTheRecursionUnwinds()
    {
        var path = RecursiveDeploymentValidator.BuildQualifiedPath(WellFormedProfile(), "node-07");

        Assert.Equal("Facility A / Zone 1 / Sub-Zone B / NODE-07", path);
    }

    [Fact]
    public void BuildQualifiedPath_ReturnsNullForAVertexNotInTheTree()
    {
        Assert.Null(RecursiveDeploymentValidator.BuildQualifiedPath(WellFormedProfile(), "node-99"));
    }

    [Fact]
    public void Find_LocatesAVertexAtAnyDepth()
    {
        var found = RecursiveDeploymentValidator.Find(WellFormedProfile(), "sub-b");

        Assert.NotNull(found);
        Assert.Equal("Sub-Zone B", found.Name);
        Assert.Null(RecursiveDeploymentValidator.Find(WellFormedProfile(), "missing"));
    }

    [Fact]
    public void CountNodes_CountsTheWholeSubtreeIncludingTheRoot()
    {
        Assert.Equal(4, RecursiveDeploymentValidator.CountNodes(WellFormedProfile()));
    }
}
