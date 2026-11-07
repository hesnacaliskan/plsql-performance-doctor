using System.Text.RegularExpressions;
using PlsqlDoctor.Core.Findings;

namespace PlsqlDoctor.Core.Plans;

public interface IPlanRule
{
    RuleInfo Info { get; }

    IEnumerable<Finding> Analyze(ExecutionPlan plan);
}

public abstract class PlanRule : IPlanRule
{
    public abstract RuleInfo Info { get; }

    public abstract IEnumerable<Finding> Analyze(ExecutionPlan plan);

    protected Finding Create(
        PlanStep step,
        string message,
        string suggestion,
        string? example = null,
        Severity? severity = null) => new()
    {
        RuleId = Info.Id,
        Severity = severity ?? Info.Severity,
        Category = Info.Category,
        Title = Info.Title,
        Message = message,
        Suggestion = suggestion,
        Example = example,
        PlanStepId = step.Id,
        Snippet = step.Describe(),
    };
}

/// <summary>Helpers for reading Oracle predicate text such as filter(UPPER("C"."CITY")='X').</summary>
public static partial class PredicateText
{
    [GeneratedRegex(@"(?:""[^""]+""\.)?""(?<col>[^""]+)""")]
    private static partial Regex ColumnReference();

    [GeneratedRegex(@"\b(?<fn>UPPER|LOWER|TRUNC|SUBSTR|NVL|TO_CHAR|TO_NUMBER|TO_DATE|INTERNAL_FUNCTION)\((?:""[^""]+""\.)?""(?<col>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex WrappedColumnPattern();

    public static IReadOnlyList<string> Columns(IEnumerable<PlanPredicate> predicates) =>
        predicates
            .SelectMany(p => ColumnReference().Matches(p.Text))
            .Select(m => m.Groups["col"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<(string Function, string Column)> WrappedColumns(PlanPredicate predicate) =>
        WrappedColumnPattern().Matches(predicate.Text)
            .Select(m => (m.Groups["fn"].Value.ToUpperInvariant(), m.Groups["col"].Value))
            .Distinct()
            .ToList();

    public static string Shorten(string text, int max = 110) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";
}
