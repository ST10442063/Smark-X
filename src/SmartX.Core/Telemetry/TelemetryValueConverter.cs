namespace SmartX.Core.Telemetry;

/// <summary>
/// Resolves a strongly-typed payload-to-<see cref="double"/> projection for a
/// closed generic type, once per type, and caches it.
/// <para>
/// <b>Why this exists.</b> The obvious way to turn a <c>T</c> into a double is
/// <c>Convert.ToDouble(value)</c>, but that signature takes <see cref="object"/>:
/// every single call boxes the payload onto the heap. On a gateway ingesting
/// thousands of packets a second that is thousands of allocations a second, and
/// it is exactly the "expensive boxing/unboxing" trap this design avoids.
/// </para>
/// <para>
/// Instead, a <c>Func&lt;T, double&gt;</c> is built once inside the static
/// constructor. Because a static field on a generic type is separate per closed
/// type, <c>TelemetryValueConverter&lt;float&gt;</c> and
/// <c>TelemetryValueConverter&lt;int&gt;</c> each get their own delegate, resolved
/// on first use and then invoked directly - no boxing, no reflection, no type
/// test on the hot path.
/// </para>
/// </summary>
/// <typeparam name="T">A value type carried by a telemetry packet.</typeparam>
public static class TelemetryValueConverter<T>
    where T : struct
{
    /// <summary>
    /// Strongly-typed projection from the payload to a double.
    /// </summary>
    public static readonly Func<T, double> ToDouble;

    /// <summary>
    /// True when <typeparamref name="T"/> is one of the payload types the mesh publishes.
    /// </summary>
    public static bool IsSupported { get; }

    static TelemetryValueConverter()
    {
        // Each branch builds a delegate over the concrete type and then casts the
        // delegate object across to Func<T, double>. The cast is only reached on the
        // branch where T already *is* that concrete type, so it always succeeds - and
        // it happens exactly once, in this static constructor.
        var resolved = ResolveConverter();
        IsSupported = resolved is not null;

        ToDouble = resolved ?? (_ => throw new NotSupportedException(
            $"Telemetry payloads of type '{typeof(T).Name}' are not supported. " +
            "Supported payloads are float, double, decimal, int, long, short, byte and bool."));
    }

    private static Func<T, double>? ResolveConverter()
    {
        if (typeof(T) == typeof(float))
        {
            return Cast<float>(v => v);
        }

        if (typeof(T) == typeof(double))
        {
            return Cast<double>(v => v);
        }

        if (typeof(T) == typeof(decimal))
        {
            return Cast<decimal>(v => (double)v);
        }

        if (typeof(T) == typeof(int))
        {
            return Cast<int>(v => v);
        }

        if (typeof(T) == typeof(long))
        {
            return Cast<long>(v => v);
        }

        if (typeof(T) == typeof(short))
        {
            return Cast<short>(v => v);
        }

        if (typeof(T) == typeof(byte))
        {
            return Cast<byte>(v => v);
        }

        if (typeof(T) == typeof(bool))
        {
            // An actuator publishes a discrete state; it charts as 0 or 1.
            return Cast<bool>(v => v ? 1d : 0d);
        }

        return null;
    }

    private static Func<T, double> Cast<TConcrete>(Func<TConcrete, double> converter)
        where TConcrete : struct
        => (Func<T, double>)(object)converter;
}
