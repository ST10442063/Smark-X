namespace SmartX.Core.Models;

/// <summary>
/// Health state of a registered node, derived from how recently it published.
/// </summary>
public enum SensorStatus
{
    /// <summary>Publishing inside the expected heartbeat window.</summary>
    Online = 0,

    /// <summary>Still publishing, but readings are drifting outside the nominal band.</summary>
    Degraded = 1,

    /// <summary>No telemetry received inside the heartbeat window - a suspected disconnect.</summary>
    Silent = 2,

    /// <summary>Deliberately retired from the mesh; excluded from health scoring.</summary>
    Decommissioned = 3
}
