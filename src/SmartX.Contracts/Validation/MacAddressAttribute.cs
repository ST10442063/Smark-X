using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace SmartX.Contracts.Validation;

/// <summary>
/// Validates that a property carries a well-formed 48-bit MAC address in any of
/// the forms device firmware reports: <c>A4:CF:12:9B:0E:77</c>,
/// <c>a4-cf-12-9b-0e-77</c>, <c>a4cf.129b.0e77</c> or bare hex.
/// <para>
/// The same rule lives in the domain library as part of normalisation. It is
/// restated here because this assembly deliberately has no project references -
/// the duplication is the price of keeping the published contract standalone, and
/// it is a dozen characters of regex rather than a piece of business logic.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed partial class MacAddressAttribute : ValidationAttribute
{
    [GeneratedRegex(@"^(?:[0-9A-Fa-f]{2}([:-])){5}[0-9A-Fa-f]{2}$|^[0-9A-Fa-f]{4}(?:\.[0-9A-Fa-f]{4}){2}$|^[0-9A-Fa-f]{12}$")]
    private static partial Regex Pattern();

    /// <summary>Creates the attribute with its default message.</summary>
    public MacAddressAttribute()
        : base("{0} must be a MAC address in the form A4:CF:12:9B:0E:77.")
    {
    }

    /// <summary>
    /// When true, a null or blank value passes. Use alongside <see cref="RequiredAttribute"/>
    /// so a missing value and a malformed value produce distinct messages.
    /// </summary>
    public bool AllowEmpty { get; set; }

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        if (value is null || (value is string blank && string.IsNullOrWhiteSpace(blank)))
        {
            return AllowEmpty;
        }

        return value is string candidate && Pattern().IsMatch(candidate.Trim());
    }
}
