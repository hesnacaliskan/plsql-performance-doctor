using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.Lexing;

namespace PlsqlDoctor.Core.PlSql.Rules;

/// <summary>PD008: dynamic SQL built by concatenation instead of bind variables.</summary>
public sealed class DynamicSqlConcatenationRule : SqlRule
{
    private static readonly HashSet<string> ClauseWords = Words("USING", "INTO", "RETURNING", "BULK");

    private static readonly HashSet<string> BuiltinFunctions = Words(
        "TO_CHAR", "TO_DATE", "TO_NUMBER", "TO_TIMESTAMP", "CHR", "UPPER", "LOWER", "INITCAP", "TRIM", "LTRIM",
        "RTRIM", "LPAD", "RPAD", "SUBSTR", "REPLACE", "CONCAT", "NVL", "ROUND", "TRUNC");

    private const string FixExample = """
        -- Values are bound, identifiers are validated
        EXECUTE IMMEDIATE
          'DELETE FROM ' || DBMS_ASSERT.SQL_OBJECT_NAME(p_table) ||
          ' WHERE order_date < :before'
          USING p_before;
        """;

    public override RuleInfo Info { get; } = new(
        "PD008",
        Severity.Critical,
        "Parsing & security",
        "Dynamic SQL without bind variables",
        "EXECUTE IMMEDIATE with values concatenated into the SQL text.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            if (!ctx.Tokens[i].IsWord("EXECUTE") || !ctx.IsWordAt(i + 1, "IMMEDIATE")) continue;

            var exprStart = i + 2;
            var exprEnd = ctx.FindTopLevel(exprStart, t => t.IsWordIn(ClauseWords));
            if (exprEnd < 0) exprEnd = ctx.StatementEnd(i);

            var unsafeConcat = IsUnsafeConcatenation(ctx, exprStart, exprEnd);

            // EXECUTE IMMEDIATE l_sql; where l_sql was built with || somewhere else.
            if (!unsafeConcat && exprEnd == exprStart + 1 && ctx.At(exprStart) is { Kind: TokenKind.Word } variable)
            {
                unsafeConcat = IsVariableBuiltByConcatenation(ctx, variable.Text);
            }

            if (!unsafeConcat) continue;

            yield return Create(
                ctx,
                ctx.Tokens[i],
                "The SQL text is built by concatenating values instead of using bind variables. Every distinct value " +
                "creates a new statement: a hard parse each time, shared pool and latch contention, and an SQL injection hole.",
                "Put placeholders (:1, :name) in the SQL text and pass the values with USING. Identifiers such as " +
                "table names cannot be bound, so validate them with DBMS_ASSERT.SQL_OBJECT_NAME or SIMPLE_SQL_NAME.",
                FixExample);
        }
    }

    private static bool IsUnsafeConcatenation(SqlContext ctx, int start, int end)
    {
        var hasConcat = false;
        var hasUnsafeOperand = false;
        for (var j = start; j < end; j++)
        {
            var t = ctx.Tokens[j];
            if (t.IsSymbol("||"))
            {
                hasConcat = true;
                continue;
            }

            // DBMS_ASSERT.XXX(...) is the accepted way to splice identifiers; skip the whole call.
            if (t.IsWord("DBMS_ASSERT") && ctx.IsSymbolAt(j + 1, ".") && ctx.IsSymbolAt(j + 3, "("))
            {
                var close = ctx.MatchingParen(j + 3);
                if (close > 0)
                {
                    j = close;
                    continue;
                }
            }

            // TO_CHAR(5) or CHR(10) is constant text: the function name is not a value, only its arguments are.
            if (t.IsWordIn(BuiltinFunctions) && ctx.IsSymbolAt(j + 1, "(")) continue;

            if (t.Kind is TokenKind.Word or TokenKind.BindVariable) hasUnsafeOperand = true;
        }

        return hasConcat && hasUnsafeOperand;
    }

    private static bool IsVariableBuiltByConcatenation(SqlContext ctx, string variable)
    {
        for (var j = 0; j < ctx.Tokens.Count - 1; j++)
        {
            var t = ctx.Tokens[j];
            if (!t.IsWord(variable) || !ctx.IsSymbolAt(j + 1, ":=")) continue;

            if (IsUnsafeConcatenation(ctx, j + 2, ctx.StatementEnd(j))) return true;
        }

        return false;
    }
}

/// <summary>PD009: FETCH ... BULK COLLECT without LIMIT.</summary>
public sealed class BulkCollectWithoutLimitRule : SqlRule
{
    private const string FixExample = """
        OPEN c_old;
        LOOP
          FETCH c_old BULK COLLECT INTO l_ids LIMIT 1000;
          EXIT WHEN l_ids.COUNT = 0;
          -- process this chunk with FORALL
        END LOOP;
        CLOSE c_old;
        """;

    public override RuleInfo Info { get; } = new(
        "PD009",
        Severity.Warning,
        "Memory",
        "BULK COLLECT without LIMIT",
        "FETCH ... BULK COLLECT or SELECT ... BULK COLLECT loads the whole result set into session memory at once.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            var t = ctx.Tokens[i];
            if (t.IsWord("FETCH"))
            {
                var end = ctx.StatementEnd(i);
                bool bulk = false, limit = false;
                for (var k = i; k < end; k++)
                {
                    if (ctx.Tokens[k].IsWord("BULK")) bulk = true;
                    if (ctx.Tokens[k].IsWord("LIMIT")) limit = true;
                }

                if (!bulk || limit) continue;

                yield return Create(
                    ctx,
                    t,
                    "BULK COLLECT without LIMIT loads the entire result set into PGA memory in one go. " +
                    "On a large table this can exhaust session memory (ORA-04030) and slow down the whole server.",
                    "Fetch in chunks inside a loop with LIMIT 100 to 1000 and process each chunk with FORALL.",
                    FixExample);
            }
            else if (t.IsWord("SELECT") && !ctx.IsSymbolAt(i - 1, "("))
            {
                // SELECT ... BULK COLLECT INTO has no LIMIT clause at all; only a row limit in the query helps.
                var bulk = ctx.FindTopLevel(i + 1, x => x.IsWord("BULK"), x => x.IsWord("FROM"));
                if (bulk < 0 || !ctx.IsWordAt(bulk + 1, "COLLECT") || IsRowLimited(ctx, i)) continue;

                yield return Create(
                    ctx,
                    t,
                    "SELECT ... BULK COLLECT INTO loads every row the query returns into PGA memory in one go, and it " +
                    "cannot take a LIMIT clause. When the table grows this can exhaust session memory (ORA-04030).",
                    "If the result can be large, open a cursor and FETCH ... BULK COLLECT ... LIMIT in a loop. If it is a " +
                    "small, bounded lookup, cap it in the query with FETCH FIRST n ROWS ONLY or suppress this finding.",
                    FixExample);
            }
        }
    }

    private static bool IsRowLimited(SqlContext ctx, int select)
    {
        var end = ctx.StatementEnd(select);
        for (var k = select; k < end; k++)
        {
            var t = ctx.Tokens[k];
            if (t.IsWord("ROWNUM")) return true;
            if (t.IsWord("FETCH") && (ctx.IsWordAt(k + 1, "FIRST") || ctx.IsWordAt(k + 1, "NEXT"))) return true;
        }

        return false;
    }
}

/// <summary>PD010: WHEN OTHERS THEN NULL silently swallows every error.</summary>
public sealed class WhenOthersThenNullRule : SqlRule
{
    public override RuleInfo Info { get; } = new(
        "PD010",
        Severity.Warning,
        "Robustness",
        "WHEN OTHERS THEN NULL",
        "Exception handler that hides every error.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            if (!ctx.Tokens[i].IsWord("WHEN") || !ctx.IsWordAt(i + 1, "OTHERS") || !ctx.IsWordAt(i + 2, "THEN") ||
                !ctx.IsWordAt(i + 3, "NULL") || !ctx.IsSymbolAt(i + 4, ";"))
            {
                continue;
            }

            yield return Create(
                ctx,
                ctx.Tokens[i],
                "Every error is swallowed. Failed rows disappear without a trace, and performance problems such as " +
                "timeouts, deadlocks or ORA-01555 become invisible.",
                "Log the error (SQLCODE, SQLERRM, DBMS_UTILITY.FORMAT_ERROR_BACKTRACE) and re-raise it with RAISE. " +
                "Only catch the specific exceptions you can actually handle.");
        }
    }
}

/// <summary>PD013: COMMIT inside a trigger (autonomous transaction per triggering row).</summary>
public sealed class CommitInTriggerRule : SqlRule
{
    private static readonly HashSet<string> CreatePrefixes = Words("CREATE", "REPLACE", "EDITIONABLE", "NONEDITIONABLE");

    private const string FixExample = """
        -- Write in the caller's transaction: no PRAGMA, no COMMIT
        CREATE OR REPLACE TRIGGER trg_orders_audit
        AFTER UPDATE OF status ON orders
        FOR EACH ROW
        BEGIN
          INSERT INTO order_status_log (order_id, old_status, new_status, changed_at)
          VALUES (:OLD.order_id, :OLD.status, :NEW.status, SYSDATE);
        END;
        """;

    public override RuleInfo Info { get; } = new(
        "PD013",
        Severity.Critical,
        "Transaction design",
        "COMMIT inside a trigger",
        "A trigger that commits, usually through PRAGMA AUTONOMOUS_TRANSACTION, once per triggering row.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            if (!ctx.Tokens[i].IsWord("TRIGGER") || ctx.At(i - 1) is not { } prev || !prev.IsWordIn(CreatePrefixes)) continue;

            // The trigger runs until the next CREATE statement or the end of the file.
            var end = i + 1;
            while (end < ctx.Tokens.Count && !(ctx.Tokens[end].IsWord("CREATE") && IsStatementStart(ctx, end))) end++;

            bool rowLevel = false, autonomous = false;
            for (var k = i; k < end; k++)
            {
                if (ctx.IsWordAt(k, "EACH") && ctx.IsWordAt(k + 1, "ROW")) rowLevel = true;
                if (ctx.IsWordAt(k, "AUTONOMOUS_TRANSACTION")) autonomous = true;
            }

            for (var k = i; k < end; k++)
            {
                if (!ctx.Tokens[k].IsWord("COMMIT") || ctx.IsSymbolAt(k - 1, ".")) continue;

                var why = autonomous
                    ? "The trigger commits in an autonomous transaction" +
                      (rowLevel ? " once for every row the triggering statement touches: one redo log sync ('log file sync') per row. " : ". ") +
                      "The committed rows also survive when the main transaction rolls back, so they can describe changes that never happened."
                    : "COMMIT is not allowed in a trigger without PRAGMA AUTONOMOUS_TRANSACTION and fails at run time with ORA-04092.";

                yield return Create(
                    ctx,
                    ctx.Tokens[k],
                    why,
                    "Let the trigger write in the caller's transaction: remove the PRAGMA and the COMMIT. If the data really " +
                    "must survive a rollback, collect the rows in a compound trigger and write them once with FORALL in AFTER STATEMENT.",
                    FixExample,
                    rowLevel || !autonomous ? Severity.Critical : Severity.Warning);
            }

            i = end - 1;
        }
    }

    private static bool IsStatementStart(SqlContext ctx, int index) =>
        index == 0 || ctx.IsSymbolAt(index - 1, "/") || ctx.IsSymbolAt(index - 1, ";");
}
