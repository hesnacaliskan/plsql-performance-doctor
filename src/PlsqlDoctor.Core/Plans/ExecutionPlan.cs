using System.Globalization;

namespace PlsqlDoctor.Core.Plans;

public sealed record PlanPredicate(string Type, string Text);

public sealed class PlanStep
{
    public int Id { get; init; }
    public int Depth { get; init; }
    public required string Operation { get; init; }
    public string? ObjectName { get; init; }

    /// <summary>"Rows" or "E-Rows": optimizer estimate per execution of this step.</summary>
    public double? EstimatedRows { get; init; }

    /// <summary>"A-Rows": actual rows over all executions (only with ALLSTATS).</summary>
    public double? ActualRows { get; init; }

    public double? Starts { get; init; }
    public double? Cost { get; init; }
    public double? Buffers { get; init; }
    public bool HasPredicateMarker { get; init; }

    public List<PlanPredicate> Predicates { get; } = [];
    public PlanStep? Parent { get; internal set; }
    public List<PlanStep> Children { get; } = [];

    public double? Rows => ActualRows ?? EstimatedRows;

    /// <summary>Cost when the plan has it, otherwise logical reads.</summary>
    public double? Weight => Cost ?? Buffers;

    public double? SelfWeight => Weight is { } w ? Math.Max(0, w - Children.Sum(c => c.Weight ?? 0)) : null;

    public bool Is(string operationFragment) =>
        Operation.Contains(operationFragment, StringComparison.OrdinalIgnoreCase);

    public string Describe()
    {
        var parts = new List<string>();
        if (Rows is { } rows) parts.Add($"rows {PlanNumber.Format(rows)}");
        if (Cost is { } cost) parts.Add($"cost {PlanNumber.Format(cost)}");
        else if (Buffers is { } buffers) parts.Add($"buffers {PlanNumber.Format(buffers)}");
        var name = string.IsNullOrEmpty(ObjectName) ? "" : $" {ObjectName}";
        var extra = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
        return $"Id {Id}: {Operation}{name}{extra}";
    }
}

public sealed class ExecutionPlan(IReadOnlyList<PlanStep> steps, string? label = null)
{
    public IReadOnlyList<PlanStep> Steps { get; } = steps;

    /// <summary>For example "SQL_ID 8h2kq0p3x5m1v, child 0" when the output names the cursor.</summary>
    public string? Label { get; } = label;

    public PlanStep? Root => Steps.Count > 0 ? Steps[0] : null;

    public bool HasCost => Steps.Any(s => s.Cost is not null);

    public bool HasActuals => Steps.Any(s => s.ActualRows is not null);

    public double? TotalWeight => Root?.Weight ?? Steps.Max(s => s.Weight);

    /// <summary>Share of the statement's total cost (or buffers) spent in this step and below.</summary>
    public double? ShareOf(PlanStep step) =>
        step.Weight is { } w && TotalWeight is > 0 and var total ? w / total : null;
}

public static class PlanNumber
{
    public static string Format(double value) => value switch
    {
        >= 1_000_000_000 => (value / 1_000_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "G",
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString("0.##", CultureInfo.InvariantCulture),
    };

    public static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
