namespace PlsqlDoctor.Core.Findings;

public enum Severity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

public enum SourceKind
{
    PlSql,
    ExecutionPlan,
}

/// <summary>Static description of a rule, used for listing and documentation.</summary>
public sealed record RuleInfo(string Id, Severity Severity, string Category, string Title, string Description);

public sealed record Finding
{
    public required string RuleId { get; init; }
    public required Severity Severity { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }

    /// <summary>What is wrong and why it is slow.</summary>
    public required string Message { get; init; }

    /// <summary>How to fix it.</summary>
    public required string Suggestion { get; init; }

    public int? Line { get; init; }
    public int? PlanStepId { get; init; }
    public string? Snippet { get; init; }

    /// <summary>Optional code showing the improved version.</summary>
    public string? Example { get; init; }
}

public sealed record AnalysisReport(
    string Source,
    SourceKind Kind,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Notes)
{
    /// <summary>Part of the source this report covers, e.g. one child cursor of a multi-plan file.</summary>
    public string? Section { get; init; }

    public string DisplayName => Section is null ? Source : $"{Source} [{Section}]";

    public int CountOf(Severity severity) => Findings.Count(f => f.Severity == severity);

    /// <summary>
    /// 0-100 score. Each finding adds a penalty (critical 15, warning 6, info 1) and the score
    /// decays exponentially, so a file never drops below 0 and one problem never looks like ten.
    /// </summary>
    public int HealthScore
    {
        get
        {
            var penalty = 15 * CountOf(Severity.Critical) + 6 * CountOf(Severity.Warning) + CountOf(Severity.Info);
            return (int)Math.Round(100 * Math.Exp(-penalty / 60.0));
        }
    }

    public Severity? MaxSeverity => Findings.Count == 0 ? null : Findings.Max(f => f.Severity);
}
