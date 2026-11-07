using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlsqlDoctor.Core.Findings;

namespace PlsqlDoctor.Core.Reporting;

/// <summary>
/// SARIF 2.1.0 output for GitHub code scanning, Azure DevOps and other tools that show
/// findings inline on pull requests.
/// </summary>
public sealed class SarifReporter(IEnumerable<RuleInfo> rules) : IReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // The file is read by tools, not embedded in HTML, so quotes and apostrophes stay readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IReadOnlyList<RuleInfo> _rules = rules.ToList();

    public void Write(IReadOnlyList<AnalysisReport> reports, TextWriter w)
    {
        var ruleIndex = _rules.Select((r, i) => (r.Id, i)).ToDictionary(x => x.Id, x => x.i);

        var results = reports.SelectMany(report => report.Findings.Select(f => WithRuleIndex(ruleIndex, f.RuleId, new Dictionary<string, object>
        {
            ["ruleId"] = f.RuleId,
            ["level"] = Level(f.Severity),
            ["message"] = new { text = MessageText(report, f) },
            ["locations"] = new[]
            {
                new
                {
                    physicalLocation = new
                    {
                        artifactLocation = ArtifactLocation(report.Source),
                        // Plan findings have no source line; SARIF consumers need one, so they point at line 1.
                        region = new { startLine = f.Line ?? 1 },
                    },
                },
            },
        }))).ToList();

        var sarif = new Dictionary<string, object>
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new
                        {
                            name = DoctorInfo.Name,
                            version = DoctorInfo.Version,
                            semanticVersion = DoctorInfo.Version,
                            rules = _rules.Select(r => new
                            {
                                id = r.Id,
                                name = r.Title,
                                shortDescription = new { text = r.Title },
                                fullDescription = new { text = r.Description },
                                defaultConfiguration = new { level = Level(r.Severity) },
                                properties = new { tags = new[] { "performance", r.Category } },
                            }),
                        },
                    },
                    results,
                },
            },
        };

        w.WriteLine(JsonSerializer.Serialize(sarif, Options));
    }

    // ruleIndex must be a valid integer when present, so a rule the reporter does not know gets none.
    private static Dictionary<string, object> WithRuleIndex(Dictionary<string, int> indexes, string ruleId, Dictionary<string, object> result)
    {
        if (indexes.TryGetValue(ruleId, out var index)) result["ruleIndex"] = index;
        return result;
    }

    private static string Level(Severity severity) => severity switch
    {
        Severity.Critical => "error",
        Severity.Warning => "warning",
        _ => "note",
    };

    private static string MessageText(AnalysisReport report, Finding f)
    {
        var where = f.PlanStepId is { } id ? $"Plan step Id {id}{(report.Section is null ? "" : $" ({report.Section})")}: " : "";
        return $"{where}{f.Message} Fix: {f.Suggestion}";
    }

    private static object ArtifactLocation(string source)
    {
        if (source == "<stdin>") return new { uri = "stdin" };
        if (Path.IsPathRooted(source)) return new { uri = new Uri(Path.GetFullPath(source)).AbsoluteUri };

        var relative = string.Join('/', source.Replace(Path.DirectorySeparatorChar, '/').Split('/').Select(Uri.EscapeDataString));
        return new { uri = relative, uriBaseId = "%SRCROOT%" };
    }
}
