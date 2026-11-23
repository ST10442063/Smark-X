using System.ComponentModel.DataAnnotations;

namespace SmartX.Contracts.Validation;

/// <summary>
/// Validates that a string is one of the values in a <see cref="SensorVocabulary"/>
/// list, matched case-insensitively.
/// <para>
/// Doing this with an attribute rather than a manual check in each controller means
/// the rejection happens during model binding, so a bad category never reaches the
/// service layer and the client gets a field-level error naming the property
/// instead of a generic 400.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class AllowedValuesFromAttribute : ValidationAttribute
{
    private readonly IReadOnlyList<string> _allowed;

    /// <summary>Restricts the property to one of the named vocabulary lists.</summary>
    /// <param name="vocabulary">One of "Categories", "Units", "Statuses" or "NodeKinds".</param>
    public AllowedValuesFromAttribute(string vocabulary)
    {
        _allowed = vocabulary switch
        {
            "Categories" => SensorVocabulary.Categories,
            "Units" => SensorVocabulary.Units,
            "Statuses" => SensorVocabulary.Statuses,
            "NodeKinds" => SensorVocabulary.NodeKinds,
            _ => throw new ArgumentException($"Unknown vocabulary '{vocabulary}'.", nameof(vocabulary))
        };

        ErrorMessage = "{0} must be one of: " + string.Join(", ", _allowed) + ".";
    }

    /// <summary>When true, a null or blank value passes.</summary>
    public bool AllowEmpty { get; set; }

    /// <inheritdoc />
    public override bool IsValid(object? value)
    {
        if (value is null || (value is string blank && string.IsNullOrWhiteSpace(blank)))
        {
            return AllowEmpty;
        }

        return value is string candidate
               && _allowed.Contains(candidate.Trim(), StringComparer.OrdinalIgnoreCase);
    }
}
