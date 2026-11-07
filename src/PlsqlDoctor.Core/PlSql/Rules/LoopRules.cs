using PlsqlDoctor.Core.Findings;

namespace PlsqlDoctor.Core.PlSql.Rules;

/// <summary>PD001: INSERT/UPDATE/DELETE/MERGE executed once per loop iteration ("slow-by-slow").</summary>
public sealed class RowByRowDmlRule : SqlRule
{
    private static readonly HashSet<string> Dml = Words("INSERT", "UPDATE", "DELETE", "MERGE");

    // "FOR UPDATE", trigger events like "BEFORE UPDATE OR DELETE OF ..."
    private static readonly HashSet<string> NotAStatement = Words("FOR", "BEFORE", "AFTER", "OR", "OF", "INSTEAD");

    private const string FixExample = """
        -- Set-based: one statement, one context switch
        INSERT INTO target_table (id, status, created_at)
        SELECT s.id, s.status, SYSDATE
          FROM source_table s
         WHERE s.status = 'NEW';

        -- Bulk: when per-row PL/SQL logic is really needed
        OPEN c_source;
        LOOP
          FETCH c_source BULK COLLECT INTO l_rows LIMIT 1000;
          EXIT WHEN l_rows.COUNT = 0;
          FORALL i IN 1 .. l_rows.COUNT
            INSERT INTO target_table VALUES l_rows(i);
        END LOOP;
        CLOSE c_source;
        """;

    public override RuleInfo Info { get; } = new(
        "PD001",
        Severity.Critical,
        "Context switching",
        "Row-by-row DML inside a loop",
        "INSERT/UPDATE/DELETE/MERGE executed once per loop iteration instead of once per set.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            var t = ctx.Tokens[i];
            if (ctx.LoopDepth[i] == 0 || !t.IsWordIn(Dml)) continue;

            var prev = ctx.At(i - 1);
            if (prev is not null && (prev.IsSymbol(".") || prev.IsWordIn(NotAStatement))) continue;

            // FORALL ... INSERT is already a bulk operation.
            var start = ctx.StatementStart(i);
            var isForall = false;
            for (var k = start; k < i; k++)
            {
                if (ctx.Tokens[k].IsWord("FORALL")) isForall = true;
            }

            if (isForall) continue;

            yield return Create(
                ctx,
                t,
                $"{t.Text.ToUpperInvariant()} runs once per iteration of the loop that starts at line {ctx.LoopStartLine[i]}. " +
                "Each execution is a separate PL/SQL-to-SQL context switch, so 1 million rows means 1 million statement executions.",
                "Replace the loop with one set-based statement (INSERT ... SELECT, MERGE, or UPDATE with a join). " +
                "If per-row PL/SQL logic is really needed, fetch with BULK COLLECT ... LIMIT and write with FORALL.",
                FixExample);
        }
    }
}

/// <summary>PD002: single-row SELECT ... INTO inside a loop (the N+1 query pattern).</summary>
public sealed class SelectIntoInLoopRule : SqlRule
{
    private const string FixExample = """
        -- Join the lookup into the driving query instead of querying it per row
        FOR r IN (SELECT o.order_id, c.segment
                    FROM orders o
                    JOIN customers c ON c.customer_id = o.customer_id
                   WHERE o.status = 'NEW') LOOP
          ...
        END LOOP;
        """;

    public override RuleInfo Info { get; } = new(
        "PD002",
        Severity.Warning,
        "Context switching",
        "SELECT ... INTO inside a loop (N+1 queries)",
        "A single-row lookup query executed on every loop iteration.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            var t = ctx.Tokens[i];
            if (ctx.LoopDepth[i] == 0 || !t.IsWord("SELECT") || ctx.IsSymbolAt(i - 1, "(")) continue;

            var into = ctx.FindTopLevel(i + 1, x => x.IsWord("INTO"), x => x.IsWord("FROM"));
            if (into < 0) continue;

            yield return Create(
                ctx,
                t,
                $"This SELECT ... INTO runs on every iteration of the loop that starts at line {ctx.LoopStartLine[i]}. " +
                "With N driving rows Oracle parses, executes and fetches N extra queries (the N+1 pattern).",
                "Join the lookup table into the loop's driving cursor, or load the lookup data once into an " +
                "associative array (TABLE OF ... INDEX BY) before the loop.",
                FixExample);
        }
    }
}

/// <summary>PD003: COMMIT inside a loop.</summary>
public sealed class CommitInLoopRule : SqlRule
{
    public override RuleInfo Info { get; } = new(
        "PD003",
        Severity.Warning,
        "Transaction design",
        "COMMIT inside a loop",
        "Committing on every iteration forces a redo log flush each time.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            var t = ctx.Tokens[i];
            if (ctx.LoopDepth[i] == 0 || !t.IsWord("COMMIT") || ctx.IsSymbolAt(i - 1, ".")) continue;

            yield return Create(
                ctx,
                t,
                $"COMMIT inside the loop that starts at line {ctx.LoopStartLine[i]} waits for a redo log write ('log file sync') on every iteration. " +
                "It also makes ORA-01555 'snapshot too old' more likely on the driving cursor and leaves half-processed data if the job fails.",
                "Commit once after the loop. For very large batches, commit per bulk chunk (for example every 10,000 rows) " +
                "together with BULK COLLECT ... LIMIT, and make the job restartable.");
        }
    }
}
