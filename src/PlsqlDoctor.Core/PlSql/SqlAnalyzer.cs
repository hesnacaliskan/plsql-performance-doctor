using System.Text.RegularExpressions;
using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.PlSql.Rules;

namespace PlsqlDoctor.Core.PlSql;

/// <summary>Runs every PL/SQL rule over a source file.</summary>
public sealed partial class SqlAnalyzer(IReadOnlyList<ISqlRule>? rules = null)
{
    public static IReadOnlyList<ISqlRule> DefaultRules { get; } =
    [
        new RowByRowDmlRule(),
        new SelectIntoInLoopRule(),
        new CommitInLoopRule(),
        new SelectStarRule(),
        new FunctionOnColumnRule(),
        new LeadingWildcardRule(),
        new NotInSubqueryRule(),
        new DynamicSqlConcatenationRule(),
        new BulkCollectWithoutLimitRule(),
        new WhenOthersThenNullRule(),
        new CountForExistenceRule(),
        new NestedCursorLoopRule(),
        new CommitInTriggerRule(),
    ];

    private readonly IReadOnlyList<ISqlRule> _rules = rules ?? DefaultRules;

    // -- plsql-doctor:ignore PD005, PD004
    [GeneratedRegex(@"plsql-doctor\s*:\s*ignore\s+(?<ids>[A-Za-z0-9_,\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex IgnoreComment();

    [GeneratedRegex(@"^(P[DX]\d{3}|ALL)$", RegexOptions.IgnoreCase)]
    private static partial Regex RuleIdPattern();

    public AnalysisReport Analyze(string source, string sourceName)
    {
        var context = new SqlContext(source, sourceName);
        var suppressed = FindSuppressions(context);

        var findings = _rules
            .SelectMany(rule => rule.Analyze(context))
            .Where(f => !IsSuppressed(f, suppressed))
            .OrderBy(f => f.Line)
            .ThenByDescending(f => f.Severity)
            .ToList();

        return new AnalysisReport(sourceName, SourceKind.PlSql, findings, []);
    }

    /// <summary>
    /// An ignore comment applies to its own line and to the next line that contains code, so blank
    /// lines and further comment lines between the comment and the code are allowed.
    /// </summary>
    private static Dictionary<int, HashSet<string>> FindSuppressions(SqlContext context)
    {
        var lines = context.Lines;
        var codeLines = context.Tokens.Select(t => t.Line).Distinct().Order().ToArray();
        var result = new Dictionary<int, HashSet<string>>();
        for (var i = 0; i < lines.Length; i++)
        {
            var match = IgnoreComment().Match(lines[i]);
            if (!match.Success) continue;

            var ids = match.Groups["ids"].Value
                .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Where(id => RuleIdPattern().IsMatch(id))
                .Select(id => id.ToUpperInvariant())
                .ToList();

            var commentLine = i + 1;
            var next = Array.Find(codeLines, l => l > commentLine);
            foreach (var line in next > 0 ? new[] { commentLine, next } : [commentLine])
            {
                if (!result.TryGetValue(line, out var set)) result[line] = set = [];
                set.UnionWith(ids);
            }
        }

        return result;
    }

    private static bool IsSuppressed(Finding finding, Dictionary<int, HashSet<string>> suppressed) =>
        finding.Line is { } line &&
        suppressed.TryGetValue(line, out var ids) &&
        (ids.Contains(finding.RuleId) || ids.Contains("ALL"));
}
