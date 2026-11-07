using System.Text;
using System.Text.Json;
using PlsqlDoctor.Cli;
using PlsqlDoctor.Core;
using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.Plans;
using PlsqlDoctor.Core.PlSql;
using PlsqlDoctor.Core.Reporting;

namespace PlsqlDoctor.Tests;

public class ReporterTests
{
    private static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);

    private static IReadOnlyList<RuleInfo> AllRules =>
        SqlAnalyzer.DefaultRules.Select(r => r.Info).Concat(PlanAnalyzer.DefaultRules.Select(r => r.Info)).ToList();

    private static List<AnalysisReport> SampleReports() =>
    [
        new SqlAnalyzer().Analyze(File.ReadAllText(SamplePath("order_processing_slow.sql")), "samples/order_processing_slow.sql"),
        new PlanAnalyzer().Analyze(File.ReadAllText(SamplePath("plans/revenue_by_city.txt")), "samples/plans/revenue_by_city.txt"),
    ];

    private static string Render(IReporter reporter, IReadOnlyList<AnalysisReport> reports)
    {
        var w = new StringWriter();
        reporter.Write(reports, w);
        return w.ToString();
    }

    [Fact]
    public void Markdown_has_a_summary_table_and_one_section_per_finding()
    {
        var reports = SampleReports();
        var md = Render(new MarkdownReporter(), reports);

        Assert.Contains("| `samples/order_processing_slow.sql` | PL/SQL |", md);
        Assert.Contains("| `samples/plans/revenue_by_city.txt` | Execution plan |", md);
        var headings = md.Split('\n').Count(l => l.StartsWith("### "));
        Assert.Equal(reports.Sum(r => r.Findings.Count), headings);
        Assert.Contains("### 🔴 PD001: Row-by-row DML inside a loop (line 23)", md);
        Assert.Contains("<details><summary>Example</summary>", md);
    }

    [Fact]
    public void Markdown_reports_clean_files()
    {
        var report = new SqlAnalyzer().Analyze(File.ReadAllText(SamplePath("order_processing_fixed.sql")), "fixed.sql");
        Assert.Contains("No performance problems found.", Render(new MarkdownReporter(), [report]));
    }

    [Fact]
    public void Sarif_is_valid_2_1_0_with_rules_levels_and_locations()
    {
        var reports = SampleReports();
        using var sarif = JsonDocument.Parse(Render(new SarifReporter(AllRules), reports));
        var root = sarif.RootElement;

        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        var run = root.GetProperty("runs")[0];
        var driver = run.GetProperty("tool").GetProperty("driver");
        Assert.Equal(DoctorInfo.Version, driver.GetProperty("version").GetString());
        var rules = driver.GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(AllRules.Count, rules.Count);

        var results = run.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(reports.Sum(r => r.Findings.Count), results.Count);
        foreach (var result in results)
        {
            var index = result.GetProperty("ruleIndex").GetInt32();
            Assert.Equal(rules[index].GetProperty("id").GetString(), result.GetProperty("ruleId").GetString());
            Assert.Contains(result.GetProperty("level").GetString(), new[] { "error", "warning", "note" });
            Assert.True(result.GetProperty("locations")[0].GetProperty("physicalLocation")
                .GetProperty("region").GetProperty("startLine").GetInt32() >= 1);
        }

        var pd001 = results.First(r => r.GetProperty("ruleId").GetString() == "PD001");
        var location = pd001.GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.Equal("error", pd001.GetProperty("level").GetString());
        Assert.Equal("samples/order_processing_slow.sql", location.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal("%SRCROOT%", location.GetProperty("artifactLocation").GetProperty("uriBaseId").GetString());
        Assert.Equal(18, location.GetProperty("region").GetProperty("startLine").GetInt32());

        var px001 = results.First(r => r.GetProperty("ruleId").GetString() == "PX001");
        Assert.StartsWith("Plan step Id 4: ", px001.GetProperty("message").GetProperty("text").GetString());
    }

    [Fact]
    public void Cli_writes_sarif()
    {
        var stdout = new StringWriter();
        var code = DoctorCli.Run(["analyze", SamplePath("order_processing_slow.sql"), "-f", "sarif", "--fail-on", "none"], stdout, new StringWriter());

        Assert.Equal(DoctorCli.Ok, code);
        using var sarif = JsonDocument.Parse(stdout.ToString());
        Assert.NotEmpty(sarif.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray());
    }

    [Fact]
    public void Version_comes_from_the_project_file()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", DoctorInfo.Version);
        Assert.DoesNotContain("+", DoctorInfo.Version);

        var stdout = new StringWriter();
        DoctorCli.Run(["--version"], stdout, new StringWriter());
        Assert.Equal(DoctorInfo.Version, stdout.ToString().Trim());
    }

    [Fact]
    public void Ansi_encoded_sources_are_decoded_with_their_code_page()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string sql = "-- müşteri listesi\nSELECT * FROM müşteriler;\n";
        var path = Path.Combine(Path.GetTempPath(), $"plsql-doctor-{Guid.NewGuid():N}.sql");
        File.WriteAllBytes(path, Encoding.GetEncoding(1254).GetBytes(sql));

        try
        {
            var stdout = new StringWriter();
            DoctorCli.Run(["analyze", path, "-f", "json", "--encoding", "windows-1254"], stdout, new StringWriter());

            using var json = JsonDocument.Parse(stdout.ToString());
            var finding = json.RootElement.GetProperty("reports")[0].GetProperty("findings")[0];
            Assert.Equal("SELECT * FROM müşteriler;", finding.GetProperty("snippet").GetString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Auto_encoding_reads_bom_and_utf8_and_falls_back_for_ansi()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "SELECT 'çğış' FROM dual;";

        Assert.Equal(text, SourceReader.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(), null));
        Assert.Equal(text, SourceReader.Decode(Encoding.UTF8.GetBytes(text), null));
        Assert.Equal(text, SourceReader.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(), null));

        // Invalid UTF-8 never throws: it is decoded with the ANSI code page instead.
        var ansi = SourceReader.Decode(Encoding.GetEncoding(1254).GetBytes(text), null);
        Assert.StartsWith("SELECT '", ansi);
        Assert.EndsWith("' FROM dual;", ansi);
    }

    [Fact]
    public void Unknown_encoding_is_a_usage_error()
    {
        var stderr = new StringWriter();
        Assert.Equal(DoctorCli.UsageError, DoctorCli.Run(["analyze", "x.sql", "--encoding", "klingon"], new StringWriter(), stderr));
        Assert.Contains("klingon", stderr.ToString());
    }
}
