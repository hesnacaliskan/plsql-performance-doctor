using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.Plans.Rules;

namespace PlsqlDoctor.Core.Plans;

/// <summary>Runs every plan rule over a DBMS_XPLAN text output.</summary>
public sealed class PlanAnalyzer(IReadOnlyList<IPlanRule>? rules = null)
{
    public static IReadOnlyList<IPlanRule> DefaultRules { get; } =
    [
        new FullTableScanRule(),
        new CartesianJoinRule(),
        new FunctionOnColumnPredicateRule(),
        new CardinalityMisestimateRule(),
        new NestedLoopsLargeOuterRule(),
        new IndexSkipScanRule(),
    ];

    private readonly IReadOnlyList<IPlanRule> _rules = rules ?? DefaultRules;

    public AnalysisReport Analyze(string planText, string sourceName)
    {
        var plan = PlanParser.Parse(planText);
        if (plan.Steps.Count == 0)
        {
            return new AnalysisReport(sourceName, SourceKind.ExecutionPlan, [],
                ["No plan table found. Paste the output of DBMS_XPLAN.DISPLAY or DBMS_XPLAN.DISPLAY_CURSOR."]);
        }

        var findings = _rules
            .SelectMany(rule => rule.Analyze(plan))
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.PlanStepId)
            .ToList();

        return new AnalysisReport(sourceName, SourceKind.ExecutionPlan, findings, BuildNotes(plan));
    }

    private static List<string> BuildNotes(ExecutionPlan plan)
    {
        var notes = new List<string>();
        var label = plan.HasCost ? "cost" : "buffer gets";

        if (plan.TotalWeight is { } total)
        {
            notes.Add($"{plan.Steps.Count} plan steps, total {label} {PlanNumber.Format(total)}.");

            var hot = plan.Steps
                .Where(s => s.Id != 0 && s.SelfWeight is not null)
                .MaxBy(s => s.SelfWeight);
            if (hot?.SelfWeight is { } self && total > 0)
            {
                notes.Add($"Hottest step: {hot.Describe()}, {PlanNumber.Percent(self / total)} of the total {label}.");
            }
        }
        else
        {
            notes.Add($"{plan.Steps.Count} plan steps.");
        }

        notes.Add(plan.HasActuals
            ? "Actual row counts found (ALLSTATS): estimate-vs-actual checks are enabled."
            : "Tip: run the query with the /*+ GATHER_PLAN_STATISTICS */ hint and DBMS_XPLAN.DISPLAY_CURSOR(format => 'ALLSTATS LAST') to compare estimated and actual rows.");
        return notes;
    }
}
