using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.Lexing;

namespace PlsqlDoctor.Core.PlSql.Rules;

/// <summary>PD004: SELECT * reads every column.</summary>
public sealed class SelectStarRule : SqlRule
{
    public override RuleInfo Info { get; } = new(
        "PD004",
        Severity.Info,
        "Unnecessary work",
        "SELECT *",
        "Selecting every column instead of the ones that are needed.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            if (!ctx.Tokens[i].IsWord("SELECT") || !ctx.IsSymbolAt(i + 1, "*")) continue;

            // EXISTS (SELECT * ...) never reads the columns, so it is harmless.
            if (ctx.IsSymbolAt(i - 1, "(") && ctx.IsWordAt(i - 2, "EXISTS")) continue;

            yield return Create(
                ctx,
                ctx.Tokens[i],
                "SELECT * reads every column: more I/O, more network traffic and PGA memory, no index-only access, " +
                "and the code silently changes behaviour when columns are added to the table.",
                "List only the columns you actually use.");
        }
    }
}

/// <summary>PD005: a function wrapped around a column in a predicate disables normal indexes.</summary>
public sealed class FunctionOnColumnRule : SqlRule
{
    private static readonly HashSet<string> PredicateStarters = Words("WHERE", "AND", "OR", "ON");

    private static readonly HashSet<string> Functions = Words(
        "UPPER", "LOWER", "TRUNC", "TO_CHAR", "TO_NUMBER", "TO_DATE", "NVL", "COALESCE",
        "SUBSTR", "TRIM", "LTRIM", "RTRIM");

    private static readonly HashSet<string> NotColumns = Words(
        "SYSDATE", "SYSTIMESTAMP", "CURRENT_DATE", "CURRENT_TIMESTAMP", "NULL", "USER");

    private static readonly HashSet<string> ComparisonWords = Words("LIKE", "IN", "BETWEEN", "IS", "NOT");

    private static readonly HashSet<string> ComparisonSymbols = ["=", "<", ">", "<=", ">=", "<>", "!=", "^="];

    // Common naming conventions for PL/SQL parameters and variables.
    private static readonly string[] VariablePrefixes = ["p_", "l_", "v_", "g_", "c_", "gc_", "lc_", "i_", "o_"];

    public override RuleInfo Info { get; } = new(
        "PD005",
        Severity.Warning,
        "Index usage",
        "Function applied to a column in a predicate",
        "UPPER(col) = ..., TRUNC(date_col) = ... and similar predicates prevent normal B-tree index access.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            var fn = ctx.Tokens[i];
            if (!fn.IsWordIn(Functions) || !ctx.IsSymbolAt(i + 1, "(")) continue;

            var prev = ctx.At(i - 1);
            if (prev is null || !prev.IsWordIn(PredicateStarters)) continue;

            var arg = ctx.At(i + 2);
            if (arg is null || !arg.IsIdentifier || arg.IsWordIn(NotColumns)) continue;

            var column = arg.Text;
            var next = i + 3;
            if (ctx.IsSymbolAt(next, ".") && ctx.At(next + 1) is { IsIdentifier: true } qualified)
            {
                column = $"{arg.Text}.{qualified.Text}";
                next += 2;
            }

            if (ctx.IsSymbolAt(next, "(")) continue; // argument is itself a function call

            var bare = column.Contains('.') ? column[(column.LastIndexOf('.') + 1)..] : column;
            if (VariablePrefixes.Any(p => bare.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

            var close = ctx.MatchingParen(i + 1);
            var after = ctx.At(close + 1);
            if (close < 0 || after is null) continue;

            var isComparison = (after.Kind == TokenKind.Symbol && ComparisonSymbols.Contains(after.Text))
                               || after.IsWordIn(ComparisonWords);
            if (!isComparison) continue;

            var name = fn.Text.ToUpperInvariant();
            yield return Create(
                ctx,
                fn,
                $"{name}({column}) is evaluated for every row, so a normal index on {bare} cannot be used " +
                "and the optimizer usually falls back to a full table scan.",
                SuggestionFor(name, column, bare));
        }
    }

    private static string SuggestionFor(string fn, string column, string bare) => fn switch
    {
        "TRUNC" =>
            $"Rewrite it as a range on the raw column: {column} >= :day AND {column} < :day + 1. " +
            $"The optimizer can then use an index range scan on {bare}.",
        "UPPER" or "LOWER" =>
            $"Create a function-based index, e.g. CREATE INDEX ix_{bare.ToLowerInvariant()}_{fn.ToLowerInvariant()} " +
            $"ON <table> ({fn}({bare.ToLowerInvariant()})); or store a normalized copy of the column.",
        "TO_CHAR" or "TO_DATE" or "TO_NUMBER" =>
            $"Compare {column} in its native data type and convert the other side instead, " +
            $"e.g. {column} >= DATE '2026-01-01' rather than TO_CHAR({column}, 'YYYY') = '2026'.",
        "NVL" or "COALESCE" =>
            $"Rewrite as ({column} = :value OR {column} IS NULL), or index the expression with a function-based index.",
        "SUBSTR" =>
            $"For prefix checks use {column} LIKE 'ABC%', which can use an index range scan.",
        _ =>
            "Clean the data when it is written (store trimmed values), or create a function-based index on the expression.",
    };
}

/// <summary>PD006: LIKE with a leading wildcard.</summary>
public sealed class LeadingWildcardRule : SqlRule
{
    public override RuleInfo Info { get; } = new(
        "PD006",
        Severity.Warning,
        "Index usage",
        "LIKE with a leading wildcard",
        "LIKE '%text' cannot use a B-tree index range scan.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            if (!ctx.Tokens[i].IsWord("LIKE")) continue;
            var pattern = ctx.At(i + 1);
            if (pattern is not { Kind: TokenKind.String } || !pattern.Text.StartsWith('%')) continue;

            var shown = $"'{pattern.Text}'" + (ctx.IsSymbolAt(i + 2, "||") ? " || ..." : "");
            yield return Create(
                ctx,
                ctx.Tokens[i],
                $"LIKE {shown} starts with a wildcard, so an index on the column cannot be used for a range scan " +
                "and every row has to be checked.",
                "If users search by prefix, drop the leading %. For real 'contains' search, use an Oracle Text " +
                "CONTEXT index and CONTAINS(column, :text) > 0.");
        }
    }
}

/// <summary>PD007: NOT IN with a subquery.</summary>
public sealed class NotInSubqueryRule : SqlRule
{
    private const string FixExample = """
        DELETE FROM customers c
         WHERE NOT EXISTS (SELECT 1
                             FROM orders o
                            WHERE o.customer_id = c.customer_id);
        """;

    public override RuleInfo Info { get; } = new(
        "PD007",
        Severity.Warning,
        "Query semantics",
        "NOT IN with a subquery",
        "NOT IN (SELECT ...) is NULL-unsafe and can block an efficient anti-join.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            if (!ctx.Tokens[i].IsWord("NOT") || !ctx.IsWordAt(i + 1, "IN") ||
                !ctx.IsSymbolAt(i + 2, "(") || !ctx.IsWordAt(i + 3, "SELECT"))
            {
                continue;
            }

            yield return Create(
                ctx,
                ctx.Tokens[i],
                "NOT IN (SELECT ...) returns no rows at all if the subquery produces a single NULL. " +
                "When the column is nullable Oracle also cannot always turn it into an efficient hash anti-join.",
                "Use NOT EXISTS with a correlated subquery. It is NULL-safe and always eligible for an anti-join.",
                FixExample);
        }
    }
}

/// <summary>PD011: SELECT COUNT(*) INTO ... used as an existence check.</summary>
public sealed class CountForExistenceRule : SqlRule
{
    public override RuleInfo Info { get; } = new(
        "PD011",
        Severity.Info,
        "Unnecessary work",
        "COUNT(*) used as an existence check",
        "Counting all matching rows when only 'is there at least one?' matters.");

    public override IEnumerable<Finding> Analyze(SqlContext ctx)
    {
        for (var i = 0; i < ctx.Tokens.Count; i++)
        {
            if (!ctx.Tokens[i].IsWord("SELECT") || !ctx.IsWordAt(i + 1, "COUNT") || !ctx.IsSymbolAt(i + 2, "(")) continue;

            var arg = ctx.At(i + 3);
            if (arg is null || !(arg.IsSymbol("*") || arg.Kind == TokenKind.Number)) continue;
            if (!ctx.IsSymbolAt(i + 4, ")") || !ctx.IsWordAt(i + 5, "INTO")) continue;

            var end = ctx.StatementEnd(i);
            var limited = false;
            for (var k = i; k < end; k++)
            {
                var t = ctx.Tokens[k];
                if (t.IsWord("ROWNUM") || (t.IsWord("FETCH") && ctx.IsWordAt(k + 1, "FIRST")) || t.IsWord("GROUP"))
                {
                    limited = true;
                }
            }

            if (limited) continue;

            yield return Create(
                ctx,
                ctx.Tokens[i],
                "COUNT(*) reads every matching row even though the code only needs to know whether one exists.",
                "Stop at the first row: add AND ROWNUM = 1 to the WHERE clause, or use " +
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM ... WHERE ...) THEN 1 ELSE 0 END INTO l_found FROM dual.");
        }
    }
}
