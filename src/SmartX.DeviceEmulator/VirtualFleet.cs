using SmartX.Contracts.Stream;
using SmartX.Contracts.Topology;

namespace SmartX.DeviceEmulator;

/// <summary>
/// The emulated mesh, and the small amount of physics that makes commands matter.
/// <para>
/// Nodes in the same zone affect each other the way they would in a hydroponic
/// bay: an irrigation pump or valve that is on lifts the soil moisture in its zone,
/// and a probe whose bay has dried out only recovers once one is switched on. A
/// lighting meter follows its zone's grow-light relay, so switching the relay off
/// ends a spike. That is what turns the predictive engine's "switch Pump A on"
/// into something you can watch work.
/// </para>
/// </summary>
public sealed class VirtualFleet
{
    private readonly List<VirtualDevice> _devices = [];
    private readonly Dictionary<string, VirtualDevice> _byMac = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();
    private readonly object _gate = new();
    private (string Mac, double X, double Y)? _gateway;

    /// <summary>Every node.</summary>
    public IReadOnlyList<VirtualDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _devices];
            }
        }
    }

    /// <summary>Adds nodes from a manifest that are not already in the fleet.</summary>
    /// <returns>How many were added.</returns>
    public int Merge(DeviceManifestResponse manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        lock (_gate)
        {
            RememberGateway(manifest);
            var added = 0;
            foreach (var entry in manifest.Devices)
            {
                if (_byMac.ContainsKey(entry.MacAddress))
                {
                    continue;
                }

                var device = new VirtualDevice(entry);
                _devices.Add(device);
                _byMac[device.MacAddress] = device;
                added++;
            }

            return added;
        }
    }

    /// <summary>
    /// Replaces every node with a fresh one from the manifest, after the gateway has
    /// restarted. Nodes the operator took offline stay offline.
    /// </summary>
    public void Reset(DeviceManifestResponse manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        lock (_gate)
        {
            var offline = _devices.Where(device => !device.Online).Select(device => device.MacAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
            RememberGateway(manifest);

            _devices.Clear();
            _byMac.Clear();

            foreach (var entry in manifest.Devices)
            {
                var device = new VirtualDevice(entry) { Online = !offline.Contains(entry.MacAddress) };
                _devices.Add(device);
                _byMac[device.MacAddress] = device;
            }
        }
    }

    /// <summary>A node by address.</summary>
    public VirtualDevice? Find(string mac)
    {
        lock (_gate)
        {
            return _byMac.GetValueOrDefault(mac);
        }
    }

    /// <summary>The first node matching a preference list, by name.</summary>
    public VirtualDevice? Pick(Func<VirtualDevice, bool> eligible, params string[] preferredNames)
    {
        lock (_gate)
        {
            foreach (var name in preferredNames)
            {
                var named = _devices.FirstOrDefault(device =>
                    eligible(device) && string.Equals(device.DisplayName, name, StringComparison.OrdinalIgnoreCase));
                if (named is not null)
                {
                    return named;
                }
            }

            return _devices.FirstOrDefault(eligible);
        }
    }

    /// <summary>Addresses of the nodes currently online.</summary>
    public List<string> OnlineMacs()
    {
        lock (_gate)
        {
            return [.. _devices.Where(device => device.Online).Select(device => device.MacAddress)];
        }
    }

    /// <summary>Advances every online node one tick and returns the frame to publish.</summary>
    public UplinkEnvelopeRequest Tick(string gatewayId)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            foreach (var device in _devices.Where(device => device.Online && !device.IsActuator))
            {
                Advance(device);
            }

            return new UplinkEnvelopeRequest
            {
                GatewayId = gatewayId,
                Packets = [.. _devices.Where(device => device.Online).Select(device => device.ToPacket(now))]
            };
        }
    }

    /// <summary>One out-of-cycle packet, for an incident that should be seen at once.</summary>
    public UplinkEnvelopeRequest Immediate(string gatewayId, VirtualDevice device)
    {
        lock (_gate)
        {
            return new UplinkEnvelopeRequest { GatewayId = gatewayId, Packets = [device.ToPacket(DateTimeOffset.UtcNow)] };
        }
    }

    /// <summary>Hundreds of routine readings at once, to back the standard lane up.</summary>
    public UplinkEnvelopeRequest Flood(string gatewayId, int count)
    {
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var online = _devices.Where(device => device.Online).ToList();
            var packets = new List<UplinkPacketRequest>(count);

            for (var i = 0; i < count && online.Count > 0; i++)
            {
                packets.Add(online[i % online.Count].ToPacket(now));
            }

            return new UplinkEnvelopeRequest { GatewayId = gatewayId, Packets = packets };
        }
    }

    /// <summary>Applies a relay state from a downlink command.</summary>
    public bool Apply(string mac, bool state)
    {
        lock (_gate)
        {
            if (!_byMac.TryGetValue(mac, out var device) || !device.IsActuator)
            {
                return false;
            }

            device.RelayOn = state;
            return true;
        }
    }

    // Callers hold the lock.
    private void Advance(VirtualDevice device)
    {
        var jitter = Gaussian();

        if (device.IsMoistureProbe)
        {
            var irrigating = _devices.Any(other => other.Irrigates && other.RelayOn && other.Online && other.Zone == device.Zone);

            if (device.Mode == DeviceMode.Crashed)
            {
                if (irrigating)
                {
                    // Water is reaching the bay: recover a few percent a tick.
                    device.Value = Math.Min(device.Baseline, device.Value + 3d + Math.Abs(jitter));
                    if (device.Value >= device.Baseline - 0.5d)
                    {
                        device.Mode = DeviceMode.Normal;
                    }
                }
                else
                {
                    // Still drying out.
                    device.Value = Math.Max(3d, device.Value - 0.1d + (jitter * 0.2d));
                }

                return;
            }

            var target = device.Baseline + (irrigating ? 6d : 0d);
            device.Value = Math.Clamp(device.Value + ((target - device.Value) * 0.1d) + (jitter * 0.6d), 0d, 100d);
            return;
        }

        if (device.IsPowerMeter)
        {
            var lightsOff = device.IsLightingMeter
                            && _devices.Any(other => other.IsLightingRelay && other.Zone == device.Zone && !other.RelayOn);

            if (device.Mode == DeviceMode.Spiking)
            {
                if (lightsOff || --device.SpikeTicks <= 0)
                {
                    device.Mode = DeviceMode.Normal;
                    device.SpikeTicks = 0;
                }
                else
                {
                    device.Value = (device.Baseline * 3.4d) + (jitter * 40d);
                    return;
                }
            }

            var target = lightsOff ? device.Baseline * 0.25d : device.Baseline;
            device.Value = Math.Max(0d, device.Value + ((target - device.Value) * 0.25d) + (jitter * 12d));
            return;
        }

        if (device.Category == "Connectivity")
        {
            device.Value = Math.Clamp(device.Value + ((device.Baseline - device.Value) * 0.1d) + (jitter * 1.5d), -95d, -30d);
            return;
        }

        device.Value = Math.Clamp(device.Value + ((device.Baseline - device.Value) * 0.1d) + (jitter * 0.5d), 0d, 100d);
    }

    /// <summary>
    /// Every online node's neighbour table: each node it can hear, at the signal
    /// strength the log-distance path-loss model gives for the distance between
    /// them, plus Gaussian shadowing of about 1.5 dB - the reading-to-reading
    /// wobble real radios show as people, water and foliage move between them.
    /// <para>
    /// The model is the gateway's <c>LinkCostModel</c>, restated here because the
    /// emulator, like real firmware, only shares the wire contracts with the
    /// gateway, not its domain library. A node taken offline stops reporting and
    /// stops being heard, exactly as a dead radio would.
    /// </para>
    /// </summary>
    /// <returns>The tables, or null when the gateway supplied no positions to model.</returns>
    public NeighbourEnvelopeRequest? NeighbourTables(string gatewayId)
    {
        lock (_gate)
        {
            var placed = _devices.Where(device => device.Online && device.X is not null && device.Y is not null).ToList();
            if (placed.Count == 0)
            {
                return null;
            }

            var hearable = placed.Select(device => (Mac: device.MacAddress, X: device.X!.Value, Y: device.Y!.Value)).ToList();
            if (_gateway is { } gateway)
            {
                hearable.Add(gateway);
            }

            var reports = new List<NeighbourReportRequest>(placed.Count);
            foreach (var device in placed)
            {
                var neighbours = new List<NeighbourEntryRequest>();

                foreach (var other in hearable)
                {
                    if (string.Equals(other.Mac, device.MacAddress, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var distance = Math.Sqrt(Math.Pow(other.X - device.X!.Value, 2) + Math.Pow(other.Y - device.Y!.Value, 2));
                    var expected = RssiAt(distance);

                    // A node only lists neighbours it can hold a link with on average.
                    // Testing the noisy sample instead would let a node at the edge of
                    // range flicker in and out of the table on lucky readings.
                    if (expected >= -90d)
                    {
                        var rssi = Math.Round(expected + (Gaussian() * 1.5d));
                        neighbours.Add(new NeighbourEntryRequest { MacAddress = other.Mac, RssiDbm = rssi });
                    }
                }

                reports.Add(new NeighbourReportRequest { MacAddress = device.MacAddress, Neighbours = [.. neighbours.Take(64)] });
            }

            return new NeighbourEnvelopeRequest { GatewayId = gatewayId, Reports = reports };
        }
    }

    /// <summary>Log-distance path loss: -40 dBm at one metre, exponent 2.7.</summary>
    private static double RssiAt(double distanceMetres) => -40d - (27d * Math.Log10(Math.Max(1d, distanceMetres)));

    private void RememberGateway(DeviceManifestResponse manifest)
    {
        if (manifest.GatewayMac is { } mac && manifest.GatewayX is { } x && manifest.GatewayY is { } y)
        {
            _gateway = (mac, x, y);
        }
    }

    /// <summary>A standard normal sample, by the Box-Muller transform.</summary>
    private double Gaussian()
    {
        var u1 = 1d - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2d * Math.Log(u1)) * Math.Cos(2d * Math.PI * u2);
    }
}
