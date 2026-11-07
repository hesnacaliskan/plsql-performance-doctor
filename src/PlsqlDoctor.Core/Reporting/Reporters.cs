using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlsqlDoctor.Core.Findings;

namespace PlsqlDoctor.Core.Reporting;

public interface IReporter
{
    void Write(IReadOnlyList<AnalysisReport> reports, TextWriter writer);
}

/// <summary>Human-friendly terminal output with optional ANSI colors.</summary>
public sealed class ConsoleReporter(bool useColor, bool showExamples) : IReporter
{
    private const int Width = 92;

    public void Write(IReadOnlyList<AnalysisReport> reports, TextWriter w)
    {
        w.WriteLine(Paint("1", $"{DoctorInfo.Name} v{DoctorInfo.Version}"));
        foreach (var report in reports) WriteReport(report, w);

        if (reports.Count > 1)
        {
            var all = reports.SelectMany(r => r.Findings).ToList();
            w.WriteLine();
            w.WriteLine(new string('=', Width));
            w.WriteLine($"{reports.Count} sources analyzed: {Counts(all)}");
        }
    }

    private void WriteReport(AnalysisReport r, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine(new string('-', Width));
        var kind = r.Kind == SourceKind.PlSql ? "PL/SQL" : "execution plan";
        w.WriteLine($"{Paint("1", r.DisplayName)}  [{kind}]");
        w.WriteLine($"Health score {Score(r.HealthScore)}   {Counts(r.Findings)}");
        foreach (var note in r.Notes) WriteWrapped(w, "  * ", "    ", note, "2");

        if (r.Findings.Count == 0)
        {
            w.WriteLine();
            w.WriteLine(Paint("32", "  No performance problems found."));
            return;
        }

        foreach (var f in r.Findings)
        {
            w.WriteLine();
            var location = f.Line is { } line ? $"line {line}" : f.PlanStepId is { } id ? $"step Id {id}" : "";
            w.WriteLine($"{Badge(f.Severity)} {Paint("1", f.RuleId)}  {f.Title}  {Paint("2", location)}");
            if (!string.IsNullOrWhiteSpace(f.Snippet)) w.WriteLine(Paint("2", $"           | {f.Snippet}"));
            WriteWrapped(w, "           Why  ", "                ", f.Message);
            WriteWrapped(w, "           Fix  ", "                ", f.Suggestion, "32");

            if (showExamples && f.Example is not null)
            {
                foreach (var exampleLine in f.Example.Split('\n'))
                {
                    w.WriteLine(Paint("36", "                " + exampleLine.TrimEnd()));
                }
            }
        }
    }

    private void WriteWrapped(TextWriter w, string firstPrefix, string prefix, string text, string? color = null)
    {
        var first = true;
        foreach (var line in TextWrap.Wrap(text, Width - prefix.Length))
        {
            w.WriteLine((first ? firstPrefix : prefix) + (color is null ? line : Paint(color, line)));
            first = false;
        }
    }

    private string Badge(Severity severity) => severity switch
    {
        Severity.Critical => useColor ? "\u001b[41;97m CRITICAL \u001b[0m" : "[CRITICAL]",
        Severity.Warning => useColor ? "\u001b[43;30m WARNING  \u001b[0m" : "[WARNING] ",
        _ => useColor ? "\u001b[44;97m INFO     \u001b[0m" : "[INFO]    ",
    };

    private string Score(int score)
    {
        var color = score >= 80 ? "32" : score >= 50 ? "33" : "31";
        return Paint("1;" + color, $"{score}/100");
    }

    private string Counts(IReadOnlyCollection<Finding> findings) =>
        $"{Paint("31", $"{findings.Count(f => f.Severity == Severity.Critical)} critical")}, " +
        $"{Paint("33", $"{findings.Count(f => f.Severity == Severity.Warning)} warning")}, " +
        $"{Paint("34", $"{findings.Count(f => f.Severity == Severity.Info)} info")}";

    private string Paint(string code, string text) => useColor ? $"\u001b[{code}m{text}\u001b[0m" : text;
}

/// <summary>GitHub-flavored Markdown, suitable for PR comments and job summaries.</summary>
public sealed class MarkdownReporter : IReporter
{
    public void Write(IReadOnlyList<AnalysisReport> reports, TextWriter w)
    {
        w.WriteLine($"# {DoctorInfo.Name} report");
        w.WriteLine();
        w.WriteLine("| Source | Type | Health | Critical | Warning | Info |");
        w.WriteLine("|---|---|---:|---:|---:|---:|");
        foreach (var r in reports)
        {
            w.WriteLine($"| `{r.DisplayName}` | {KindName(r.Kind)} | {r.HealthScore}/100 | {r.CountOf(Severity.Critical)} | " +
                        $"{r.CountOf(Severity.Warning)} | {r.CountOf(Severity.Info)} |");
        }

        foreach (var r in reports)
        {
            w.WriteLine();
            w.WriteLine($"## `{r.DisplayName}`");
            w.WriteLine();
            foreach (var note in r.Notes) w.WriteLine($"- {note}");
            if (r.Notes.Count > 0) w.WriteLine();

            if (r.Findings.Count == 0)
            {
                w.WriteLine("No performance problems found.");
                continue;
            }

            foreach (var f in r.Findings)
            {
                var location = f.Line is { } line ? $"line {line}" : f.PlanStepId is { } id ? $"step Id {id}" : "";
                w.WriteLine($"### {Icon(f.Severity)} {f.RuleId}: {f.Title} ({location})");
                w.WriteLine();
                if (!string.IsNullOrWhiteSpace(f.Snippet))
                {
                    w.WriteLine(f.Line is null ? "```text" : "```sql");
                    w.WriteLine(f.Snippet);
                    w.WriteLine("```");
                    w.WriteLine();
                }

                w.WriteLine($"**Why:** {f.Message}");
                w.WriteLine();
                w.WriteLine($"**Fix:** {f.Suggestion}");
                w.WriteLine();

                if (f.Example is not null)
                {
                    w.WriteLine("<details><summary>Example</summary>");
                    w.WriteLine();
                    w.WriteLine("```sql");
                    w.WriteLine(f.Example.TrimEnd());
                    w.WriteLine("```");
                    w.WriteLine();
                    w.WriteLine("</details>");
                    w.WriteLine();
                }
            }
        }
    }

    private static string KindName(SourceKind kind) => kind == SourceKind.PlSql ? "PL/SQL" : "Execution plan";

    private static string Icon(Severity severity) => severity switch
    {
        Severity.Critical => "🔴",
        Severity.Warning => "🟠",
        _ => "🔵",
    };
}

/// <summary>Machine-readable output for CI pipelines and other tools.</summary>
public sealed class JsonReporter : IReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public void Write(IReadOnlyList<AnalysisReport> reports, TextWriter w)
    {
        w.WriteLine(JsonSerializer.Serialize(
            new { tool = "plsql-performance-doctor", version = DoctorInfo.Version, reports },
            Options));
    }
}

internal static class TextWrap
{
    public static IEnumerable<string> Wrap(string text, int width)
    {
        var sb = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0 && sb.Length + 1 + word.Length > width)
            {
                yield return sb.ToString();
                sb.Clear();
            }

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(word);
        }

        if (sb.Length > 0) yield return sb.ToString();
    }
}
