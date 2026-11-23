using System.ComponentModel.DataAnnotations;
using SmartX.Contracts.Commands;

namespace SmartX.Contracts.Insights;

/// <summary>One proactive recommendation.</summary>
public sealed class SuggestionResponse
{
    /// <summary>Stable identity, used to accept or dismiss it.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>"Action" or "Insight".</summary>
    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>The evidence, in plain language.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>"Learned pattern", "Trend forecast" or "Fault history".</summary>
    public string Source { get; set; } = string.Empty;

    public double Score { get; set; }

    /// <summary>For a learned pattern, how often it has held.</summary>
    public double? Confidence { get; set; }

    /// <summary>For a learned pattern, how many times it has been seen.</summary>
    public int? Support { get; set; }

    public string? MacAddress { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>For an action, the relay state it would set.</summary>
    public bool? DesiredState { get; set; }
}

/// <summary>The current Suggested Actions and Automated Insights.</summary>
public sealed class InsightsResponse
{
    public List<SuggestionResponse> Actions { get; set; } = [];

    public List<SuggestionResponse> Insights { get; set; } = [];

    public DateTimeOffset GeneratedAtUtc { get; set; }

    /// <summary>Rules the engine currently trusts.</summary>
    public int PatternsLearned { get; set; }

    /// <summary>Alerts, searches and commands the engine has observed.</summary>
    public long EventsObserved { get; set; }

    /// <summary>How close together two events must be for one to count as following the other.</summary>
    public double WindowMinutes { get; set; }
}

/// <summary>Something the operator did that the engine should learn from.</summary>
public sealed class RecordActivityRequest
{
    /// <summary>"Search" or "Inspect".</summary>
    [Required]
    [RegularExpression("^(Search|Inspect)$", ErrorMessage = "Kind must be Search or Inspect.")]
    public string Kind { get; set; } = "Search";

    /// <summary>The search text, for a search.</summary>
    [StringLength(120)]
    public string? Text { get; set; }

    /// <summary>The device opened, for an inspect.</summary>
    public string? MacAddress { get; set; }
}

/// <summary>Names a suggestion to accept or dismiss.</summary>
public sealed class SuggestionKeyRequest
{
    [Required]
    [StringLength(120)]
    public string Key { get; set; } = string.Empty;
}

/// <summary>What accepting a suggestion did.</summary>
public sealed class SuggestionAcceptedResponse
{
    public string Message { get; set; } = string.Empty;

    /// <summary>The command it issued, for an action.</summary>
    public CommandOutcomeResponse? Command { get; set; }

    public InsightsResponse Insights { get; set; } = new();
}

/// <summary>One learned habit, for the "what the engine knows" table.</summary>
public sealed class PatternRuleResponse
{
    public string Antecedent { get; set; } = string.Empty;

    /// <summary>The antecedent in words, e.g. "Soil Moisture Probe 01 fell below its floor".</summary>
    public string AntecedentLabel { get; set; } = string.Empty;

    public string Consequent { get; set; } = string.Empty;

    public string ConsequentLabel { get; set; } = string.Empty;

    public int Support { get; set; }

    public int AntecedentCount { get; set; }

    public double Confidence { get; set; }

    public double Lift { get; set; }

    public double MeanGapSeconds { get; set; }

    /// <summary>
    /// Whether the rule clears the engine's support and confidence thresholds - only
    /// trusted rules can become suggestions. The rest are still being learned.
    /// </summary>
    public bool Trusted { get; set; }
}
