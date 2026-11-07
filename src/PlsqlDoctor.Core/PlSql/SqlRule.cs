using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.Lexing;

namespace PlsqlDoctor.Core.PlSql;

public interface ISqlRule
{
    RuleInfo Info { get; }

    IEnumerable<Finding> Analyze(SqlContext context);
}

public abstract class SqlRule : ISqlRule
{
    public abstract RuleInfo Info { get; }

    public abstract IEnumerable<Finding> Analyze(SqlContext context);

    protected Finding Create(
        SqlContext context,
        Token at,
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
        Line = at.Line,
        Snippet = context.LineText(at.Line),
    };

    protected static HashSet<string> Words(params string[] words) => new(words, StringComparer.OrdinalIgnoreCase);
}
