namespace SmartX.Client.Services;

/// <summary>
/// The Command Stream device filter: an operational category, an alert state and
/// free text, any of which may be empty.
/// </summary>
/// <param name="Category">Environmental, PowerConsumption, Actuator or Connectivity.</param>
/// <param name="Alert">Critical, Disconnected, Warning, Clear or Alerting.</param>
/// <param name="Text">Free text against name, location and address.</param>
public sealed record StreamFilter(string? Category = null, string? Alert = null, string? Text = null)
{
    /// <summary>No filter at all.</summary>
    public static StreamFilter None { get; } = new();

    /// <summary>Whether any part is set.</summary>
    public bool IsActive => !string.IsNullOrWhiteSpace(Category) || !string.IsNullOrWhiteSpace(Alert) || !string.IsNullOrWhiteSpace(Text);

    /// <summary>The filter as a query string, including the leading "?", or empty.</summary>
    public string ToQuery()
    {
        var parts = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(Category))
        {
            parts.Add($"category={Uri.EscapeDataString(Category)}");
        }

        if (!string.IsNullOrWhiteSpace(Alert))
        {
            parts.Add($"alert={Uri.EscapeDataString(Alert)}");
        }

        if (!string.IsNullOrWhiteSpace(Text))
        {
            parts.Add($"q={Uri.EscapeDataString(Text.Trim())}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }
}
