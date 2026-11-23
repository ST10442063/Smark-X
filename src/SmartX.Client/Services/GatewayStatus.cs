namespace SmartX.Client.Services;

/// <summary>
/// Whether the gateway answered the most recent call.
/// <para>
/// Every request goes through <see cref="SmartXApiClient"/>, which reports here
/// after each one. The layout subscribes and shows the "gateway not responding"
/// banner, so no individual page has to remember to check - and the banner clears
/// by itself the moment any call succeeds again.
/// </para>
/// </summary>
public sealed class GatewayStatus
{
    /// <summary>True until a call fails to reach the gateway.</summary>
    public bool IsReachable { get; private set; } = true;

    /// <summary>Raised when <see cref="IsReachable"/> flips.</summary>
    public event Action? Changed;

    /// <summary>Records the outcome of one call.</summary>
    public void Report(bool reachable)
    {
        if (reachable == IsReachable)
        {
            return;
        }

        IsReachable = reachable;
        Changed?.Invoke();
    }
}
