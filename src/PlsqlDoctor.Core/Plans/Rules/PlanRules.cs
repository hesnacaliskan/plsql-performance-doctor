using PlsqlDoctor.Core.Findings;

namespace PlsqlDoctor.Core.Plans.Rules;

/// <summary>PX001: TABLE ACCESS FULL that throws away most rows with a filter.</summary>
public sealed class FullTableScanRule : PlanRule
{
    private static readonly HashSet<string> IndexableFunctions = ["UPPER", "LOWER", "TRUNC"];

    public override RuleInfo Info { get; } = new(
        "PX001",
        Severity.Warning,
        "Access path",
        "Expensive full table scan with a filter",
        "A full scan dominates the plan while a filter discards rows: a likely missing or unusable index.");

    public override IEnumerable<Finding> Analyze(ExecutionPlan plan)
    {
        foreach (var step in plan.Steps)
        {
            if (!step.Is("TABLE ACCESS") || !step.Is("FULL")) continue;

            var share = plan.ShareOf(step);
            var filters = step.Predicates.Where(p => p.Type == "filter").ToList();
            var table = step.ObjectName ?? "the table";
            var weightLabel = plan.HasCost ? "cost" : "buffer gets";

            if (filters.Count == 0)
            {
                if (share >= 0.5 && step.Rows >= 100_000)
                {
                    yield return Create(
                        step,
                        $"{table} is read completely without any filter and accounts for {PlanNumber.Percent(share.Value)} of the statement's {weightLabel}.",
                        "This is fine if you really need every row. Otherwise check for a missing WHERE clause, " +
                        "missing partition pruning, or a join condition that should be applied earlier.",
                        severity: Severity.Info);
                }

                continue;
            }

            // A cheap full scan of a small table is often the best plan; stay quiet.
            if (share is < 0.2) continue;

            var severity = share >= 0.5 ? Severity.Critical : Severity.Warning;
            var filterText = PredicateText.Shorten(string.Join(" AND ", filters.Select(f => f.Text)));
            var shareText = share is { } s ? $" This step is {PlanNumber.Percent(s)} of the statement's {weightLabel}." : "";

            var ddl = BuildIndexDdl(step, filters);
            var wrappedNonIndexable = filters.SelectMany(PredicateText.WrappedColumns)
                .Any(w => !IndexableFunctions.Contains(w.Function));
            yield return Create(
                step,
                $"Oracle reads every block of {table} and discards non-matching rows with filter {filterText}.{shareText}",
                (wrappedNonIndexable
                    ? "The filtered column is wrapped in a function (see PX003): rewrite the predicate first, otherwise even a new index is ignored. Then "
                    : "") +
                (ddl is null
                    ? "add an index that matches the filter columns."
                    : $"index the filtered column(s) if the filter is selective: {ddl}") +
                " An index only pays off when the filter keeps a small part of the table (roughly under 5%), " +
                "so check the selectivity first.",
                ddl,
                severity);
        }
    }

    private static string? BuildIndexDdl(PlanStep step, List<PlanPredicate> filters)
    {
        if (step.ObjectName is null) return null;
        var table = step.ObjectName.ToLowerInvariant();

        var wrapped = filters.SelectMany(PredicateText.WrappedColumns)
            .FirstOrDefault(w => IndexableFunctions.Contains(w.Function));
        if (wrapped != default)
        {
            var col = wrapped.Column.ToLowerInvariant();
            return $"CREATE INDEX {IndexName(table, col + "_fn")} ON {table} ({wrapped.Function}({col}));";
        }

        var columns = PredicateText.Columns(filters).Take(3).Select(c => c.ToLowerInvariant()).ToList();
        if (columns.Count == 0) return null;
        return $"CREATE INDEX {IndexName(table, string.Join("_", columns))} ON {table} ({string.Join(", ", columns)});";
    }

    private static string IndexName(string table, string suffix)
    {
        var name = $"ix_{table}_{suffix}";
        return name.Length <= 30 ? name : name[..30];
    }
}

/// <summary>PX002: MERGE JOIN CARTESIAN.</summary>
public sealed class CartesianJoinRule : PlanRule
{
    public override RuleInfo Info { get; } = new(
        "PX002",
        Severity.Critical,
        "Join method",
        "Cartesian join",
        "Every row of one input is combined with every row of the other.");

    public override IEnumerable<Finding> Analyze(ExecutionPlan plan)
    {
        foreach (var step in plan.Steps.Where(s => s.Is("CARTESIAN")))
        {
            var sizes = step.Children.Select(c => c.Rows is { } r ? PlanNumber.Format(r) : "?").ToList();
            var product = sizes.Count == 2 ? $" ({sizes[0]} x {sizes[1]} rows)" : "";
            yield return Create(
                step,
                $"MERGE JOIN CARTESIAN combines every row of one input with every row of the other{product}. " +
                "This almost always means a missing join condition, or a badly underestimated row count.",
                "Check that every table in the FROM clause is joined to another one. If the join is correct, " +
                "gather fresh statistics: the optimizer may believe one side returns a single row.");
        }
    }
}

/// <summary>PX003: column wrapped in a function in a filter predicate (explicit or implicit conversion).</summary>
public sealed class FunctionOnColumnPredicateRule : PlanRule
{
    public override RuleInfo Info { get; } = new(
        "PX003",
        Severity.Warning,
        "Index usage",
        "Column wrapped in a function in the predicate",
        "UPPER(col), TRUNC(col) or an implicit conversion such as TO_NUMBER(col) / INTERNAL_FUNCTION(col).");

    public override IEnumerable<Finding> Analyze(ExecutionPlan plan)
    {
        foreach (var step in plan.Steps)
        {
            var wrapped = step.Predicates
                .Where(p => p.Type == "filter")
                .SelectMany(PredicateText.WrappedColumns)
                .Distinct()
                .ToList();
            if (wrapped.Count == 0) continue;

            var implicitConversion = wrapped.FirstOrDefault(w => w.Function is "INTERNAL_FUNCTION" or "TO_NUMBER");
            if (implicitConversion != default)
            {
                yield return Create(
                    step,
                    $"Oracle applies {implicitConversion.Function}() to column {implicitConversion.Column}. " +
                    "This is usually an implicit data type conversion (for example a VARCHAR2 column compared with a NUMBER, " +
                    "or a DATE compared with a TIMESTAMP), and it stops a normal index on the column from being used.",
                    "Compare the column with a value of exactly the same data type: convert the bind variable or literal, " +
                    "never the column.");
                continue;
            }

            var (fn, col) = wrapped[0];
            yield return Create(
                step,
                $"The predicate applies {fn}() to column {col}, so a regular index on {col} cannot be used for this filter.",
                fn == "TRUNC"
                    ? $"Rewrite as a range on the raw column ({col} >= :d AND {col} < :d + 1) so an index range scan is possible."
                    : $"Create a function-based index on {fn}({col.ToLowerInvariant()}), or rewrite the predicate so the column stays bare.");
        }
    }
}

/// <summary>PX004: estimated rows far away from actual rows (needs ALLSTATS LAST).</summary>
public sealed class CardinalityMisestimateRule : PlanRule
{
    private const double Ratio = 10;
    private const double MinRows = 1_000;

    public override RuleInfo Info { get; } = new(
        "PX004",
        Severity.Warning,
        "Statistics",
        "Cardinality misestimate",
        "The optimizer's row estimate is more than 10x away from the real row count.");

    public override IEnumerable<Finding> Analyze(ExecutionPlan plan)
    {
        var flagged = plan.Steps.Where(IsMisestimated).ToHashSet();

        // Report only where the error starts; parents inherit it.
        foreach (var step in flagged.Where(s => !s.Children.Any(flagged.Contains)))
        {
            var expected = step.EstimatedRows!.Value * Math.Max(1, step.Starts ?? 1);
            var actual = step.ActualRows!.Value;
            var table = step.ObjectName;
            var ddl = table is null ? null : $"EXEC DBMS_STATS.GATHER_TABLE_STATS(ownname => USER, tabname => '{table}');";
            yield return Create(
                step,
                $"The optimizer expected {PlanNumber.Format(expected)} rows but got {PlanNumber.Format(actual)}. " +
                "Every join method and join order above this step was chosen for the wrong data volume.",
                "Gather fresh statistics" + (table is null ? "" : $" on {table}") + ". If they are already fresh, the cause is " +
                "usually correlated columns or skewed data: add extended statistics (DBMS_STATS.CREATE_EXTENDED_STATS) or a histogram.",
                ddl);
        }
    }

    private static bool IsMisestimated(PlanStep step)
    {
        if (step.EstimatedRows is not { } estimated || step.ActualRows is not { } actual) return false;
        var expected = estimated * Math.Max(1, step.Starts ?? 1);
        var high = Math.Max(expected, actual);
        var low = Math.Max(1, Math.Min(expected, actual));
        return high >= MinRows && high / low >= Ratio;
    }
}

/// <summary>PX005: NESTED LOOPS driven by a large row source.</summary>
public sealed class NestedLoopsLargeOuterRule : PlanRule
{
    private const double Threshold = 10_000;

    public override RuleInfo Info { get; } = new(
        "PX005",
        Severity.Warning,
        "Join method",
        "Nested loops over a large row source",
        "The inner side of a NESTED LOOPS join is executed once per outer row.");

    public override IEnumerable<Finding> Analyze(ExecutionPlan plan)
    {
        var flagged = plan.Steps
            .Where(s => s.Is("NESTED LOOPS") && s.Children.Count >= 2 && s.Children[0].Rows >= Threshold)
            .ToHashSet();

        foreach (var step in plan.Steps.Where(flagged.Contains))
        {
            var outer = step.Children[0];
            var rows = outer.Rows!.Value;

            // Oracle often stacks two NESTED LOOPS (NLJ batching); report the inner one only.
            if (flagged.Contains(outer)) continue;
            var inner = step.Children[1];
            var estimateNote = outer.ActualRows is { } a && outer.EstimatedRows is { } e && e * 10 < a
                ? $" The optimizer chose nested loops because it expected only {PlanNumber.Format(e)} rows."
                : "";

            yield return Create(
                step,
                $"The outer input (Id {outer.Id} {outer.ObjectName ?? outer.Operation}) returns {PlanNumber.Format(rows)} rows, " +
                $"so the inner side (Id {inner.Id} {inner.ObjectName ?? inner.Operation}) runs {PlanNumber.Format(rows)} times.{estimateNote}",
                "For large inputs a HASH JOIN is usually much faster. Fix the statistics first so the optimizer sees the real " +
                "row count; use a USE_HASH hint only as a last resort.");
        }
    }
}

/// <summary>PX006: INDEX SKIP SCAN.</summary>
public sealed class IndexSkipScanRule : PlanRule
{
    public override RuleInfo Info { get; } = new(
        "PX006",
        Severity.Info,
        "Access path",
        "Index skip scan",
        "The leading column of the index is not in the predicate.");

    public override IEnumerable<Finding> Analyze(ExecutionPlan plan)
    {
        foreach (var step in plan.Steps.Where(s => s.Is("SKIP SCAN")))
        {
            yield return Create(
                step,
                $"Oracle uses index {step.ObjectName ?? "?"} without its leading column and probes it once per distinct leading value.",
                "This is fine when the leading column has very few distinct values. Otherwise create an index whose leading " +
                "column is the one you filter on.");
        }
    }
}
