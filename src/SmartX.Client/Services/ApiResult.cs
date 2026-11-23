namespace SmartX.Client.Services;

/// <summary>
/// The outcome of one call to the gateway.
/// <para>
/// Every client method returns one of these rather than throwing, because on this
/// dashboard a failed call is an ordinary thing that has to be rendered, not an
/// exceptional one. The gateway being restarted mid-demo should produce a banner
/// saying so, not an unhandled error - and a rejected registration should
/// put its messages next to the fields that caused them.
/// </para>
/// </summary>
/// <typeparam name="T">The response payload type.</typeparam>
public sealed class ApiResult<T>
{
    /// <summary>True when the gateway answered successfully.</summary>
    public bool Succeeded { get; private init; }

    /// <summary>The payload, when the call succeeded.</summary>
    public T? Value { get; private init; }

    /// <summary>HTTP status returned, or 0 when the gateway could not be reached.</summary>
    public int StatusCode { get; private init; }

    /// <summary>Short summary of what went wrong.</summary>
    public string? ErrorTitle { get; private init; }

    /// <summary>Longer explanation, when the gateway supplied one.</summary>
    public string? ErrorDetail { get; private init; }

    /// <summary>
    /// Field-level validation messages keyed by property name, so the page can put
    /// each message against the input that caused it.
    /// </summary>
    public Dictionary<string, string[]> FieldErrors { get; private init; } = [];

    /// <summary>True when the gateway could not be reached at all.</summary>
    public bool IsUnreachable { get; private init; }

    /// <summary>One line suitable for a toast or banner.</summary>
    public string ErrorMessage => ErrorDetail is { Length: > 0 }
        ? ErrorDetail
        : ErrorTitle ?? "The gateway did not respond.";

    /// <summary>A successful result.</summary>
    public static ApiResult<T> Ok(T value, int statusCode = 200) => new()
    {
        Succeeded = true,
        Value = value,
        StatusCode = statusCode
    };

    /// <summary>A result the gateway refused.</summary>
    public static ApiResult<T> Fail(
        int statusCode,
        string title,
        string? detail = null,
        Dictionary<string, string[]>? fieldErrors = null) => new()
        {
            Succeeded = false,
            StatusCode = statusCode,
            ErrorTitle = title,
            ErrorDetail = detail,
            FieldErrors = fieldErrors ?? []
        };

    /// <summary>A result where the gateway could not be reached.</summary>
    public static ApiResult<T> Unreachable(string detail) => new()
    {
        Succeeded = false,
        StatusCode = 0,
        IsUnreachable = true,
        ErrorTitle = "The Smart-X gateway is not responding.",
        ErrorDetail = detail
    };
}
