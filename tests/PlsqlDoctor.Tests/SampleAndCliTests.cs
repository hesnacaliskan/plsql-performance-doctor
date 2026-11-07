using System.Text.Json;
using PlsqlDoctor.Cli;
using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.PlSql;

namespace PlsqlDoctor.Tests;

public class SampleAndCliTests
{
    private static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);

    [Fact]
    public void Slow_sample_triggers_every_plsql_rule()
    {
        var report = new SqlAnalyzer().Analyze(File.ReadAllText(SamplePath("order_processing_slow.sql")), "slow");

        var expected = SqlAnalyzer.DefaultRules.Select(r => r.Info.Id).Order();
        Assert.Equal(expected, report.Findings.Select(f => f.RuleId).Distinct().Order());
    }

    [Fact]
    public void Fixed_sample_is_clean()
    {
        var report = new SqlAnalyzer().Analyze(File.ReadAllText(SamplePath("order_processing_fixed.sql")), "fixed");

        Assert.Empty(report.Findings);
        Assert.Equal(100, report.HealthScore);
    }

    [Fact]
    public void Cli_fails_on_critical_by_default_and_passes_with_fail_on_none()
    {
        var path = SamplePath("order_processing_slow.sql");

        Assert.Equal(DoctorCli.FindingsAboveThreshold, DoctorCli.Run(["analyze", path], new StringWriter(), new StringWriter()));
        Assert.Equal(DoctorCli.Ok, DoctorCli.Run(["analyze", path, "--fail-on", "none"], new StringWriter(), new StringWriter()));
        Assert.Equal(DoctorCli.Ok, DoctorCli.Run(["analyze", SamplePath("order_processing_fixed.sql")], new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void Cli_json_output_is_valid_and_complete()
    {
        var stdout = new StringWriter();
        DoctorCli.Run(["analyze", SamplePath("order_processing_slow.sql"), "-f", "json", "--fail-on", "none"], stdout, new StringWriter());

        using var json = JsonDocument.Parse(stdout.ToString());
        var report = json.RootElement.GetProperty("reports")[0];
        Assert.Equal("plSql", report.GetProperty("kind").GetString());
        Assert.True(report.GetProperty("findings").GetArrayLength() > 10);
    }

    [Fact]
    public void Cli_reads_plans_from_stdin()
    {
        var stdin = new StringReader(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "plans", "revenue_by_city.txt")));
        var stdout = new StringWriter();

        var code = DoctorCli.Run(["plan", "-", "-f", "markdown"], stdout, new StringWriter(), stdin);

        Assert.Equal(DoctorCli.FindingsAboveThreshold, code);
        Assert.Contains("PX001", stdout.ToString());
    }

    [Fact]
    public void Cli_reports_usage_errors()
    {
        var stderr = new StringWriter();
        Assert.Equal(DoctorCli.UsageError, DoctorCli.Run(["frobnicate"], new StringWriter(), stderr));
        Assert.Equal(DoctorCli.UsageError, DoctorCli.Run(["analyze", "does-not-exist.sql"], new StringWriter(), stderr));
        Assert.Equal(DoctorCli.UsageError, DoctorCli.Run(["analyze", "x.sql", "--fail-on", "sometimes"], new StringWriter(), stderr));
    }

    [Fact]
    public void Health_score_decreases_with_severity()
    {
        static AnalysisReport With(params Severity[] severities) => new("x", SourceKind.PlSql,
            severities.Select(s => new Finding
            {
                RuleId = "T", Severity = s, Category = "c", Title = "t", Message = "m", Suggestion = "s",
            }).ToList(), []);

        Assert.Equal(100, With().HealthScore);
        Assert.True(With(Severity.Critical).HealthScore < With(Severity.Warning).HealthScore);
        Assert.True(With(Severity.Warning).HealthScore < With(Severity.Info).HealthScore);
        Assert.InRange(With(Enumerable.Repeat(Severity.Critical, 50).ToArray()).HealthScore, 0, 1);
    }
}
