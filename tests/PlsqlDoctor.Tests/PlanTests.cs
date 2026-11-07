using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.Plans;

namespace PlsqlDoctor.Tests;

public class PlanTests
{
    private static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "plans", name));

    [Fact]
    public void Parser_reads_steps_tree_numbers_and_wrapped_predicates()
    {
        var plan = PlanParser.Parse(Sample("revenue_by_city.txt"));

        Assert.Equal(5, plan.Steps.Count);
        var orders = plan.Steps.Single(s => s.Id == 4);
        Assert.Equal("TABLE ACCESS FULL", orders.Operation);
        Assert.Equal("ORDERS", orders.ObjectName);
        Assert.Equal(96_000, orders.EstimatedRows);
        Assert.Equal(4190, orders.Cost);
        Assert.Equal(3, orders.Depth);
        Assert.Equal(2, orders.Parent!.Id);
        Assert.True(orders.HasPredicateMarker);

        var filter = Assert.Single(orders.Predicates);
        Assert.Equal("filter", filter.Type);
        Assert.Contains("'syyyy-mm-dd hh24:mi:ss'", filter.Text);
        Assert.EndsWith("hh24:mi:ss')", filter.Text);
    }

    [Theory]
    [InlineData("1200K", 1_200_000)]
    [InlineData("46M", 46_000_000)]
    [InlineData("  4210   (2)", 4210)]
    [InlineData("12", 12)]
    public void Numbers_with_units_are_parsed(string cell, double expected)
    {
        Assert.Equal(expected, PlanParser.ParseNumber(cell));
    }

    [Fact]
    public void Full_scan_plan_reports_the_dominant_scan_and_the_wrapped_columns()
    {
        var report = new PlanAnalyzer().Analyze(Sample("revenue_by_city.txt"), "plan");

        var scan = Assert.Single(report.Findings, f => f.RuleId == "PX001");
        Assert.Equal(4, scan.PlanStepId);
        Assert.Equal(Severity.Critical, scan.Severity);
        Assert.Contains("ON orders (order_date)", scan.Example);

        var wrapped = report.Findings.Where(f => f.RuleId == "PX003").Select(f => f.PlanStepId).Order();
        Assert.Equal([3, 4], wrapped.Select(id => id!.Value));
    }

    [Fact]
    public void Allstats_plan_reports_misestimate_at_its_origin_and_nested_loops_once()
    {
        var report = new PlanAnalyzer().Analyze(Sample("daily_items_allstats.txt"), "plan");

        var misestimate = Assert.Single(report.Findings, f => f.RuleId == "PX004");
        Assert.Equal(3, misestimate.PlanStepId);
        Assert.Contains("ORDER_ITEMS", misestimate.Example);

        var nestedLoops = Assert.Single(report.Findings, f => f.RuleId == "PX005");
        Assert.Equal(2, nestedLoops.PlanStepId);

        Assert.DoesNotContain(report.Findings, f => f.RuleId == "PX001");
    }

    [Fact]
    public void Cartesian_join_is_critical()
    {
        const string plan = """
            | Id  | Operation            | Name | Rows  | Cost (%CPU)|
            |   0 | SELECT STATEMENT     |      |  250K |   900   (1)|
            |   1 |  MERGE JOIN CARTESIAN|      |  250K |   900   (1)|
            |   2 |   TABLE ACCESS FULL  | A    |   500 |     3   (0)|
            |   3 |   BUFFER SORT        |      |   500 |   897   (1)|
            |   4 |    TABLE ACCESS FULL | B    |   500 |     2   (0)|
            """;

        var finding = Assert.Single(new PlanAnalyzer().Analyze(plan, "p").Findings, f => f.RuleId == "PX002");
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains("500 x 500", finding.Message);
    }

    [Fact]
    public void Text_without_a_plan_table_gives_a_helpful_note()
    {
        Assert.False(PlanParser.LooksLikePlan("SELECT * FROM dual;"));

        var report = new PlanAnalyzer().Analyze("no plan here", "p");
        Assert.Empty(report.Findings);
        Assert.Contains("DBMS_XPLAN", Assert.Single(report.Notes));
    }

    private const string TwoChildCursors = """
        SQL_ID  abc123, child number 0
        -------------------------------------
        select * from orders where status = :s

        Plan hash value: 111
        ---------------------------------------------------------
        | Id  | Operation          | Name   | Rows  | Cost (%CPU)|
        ---------------------------------------------------------
        |   0 | SELECT STATEMENT   |        |       |  5000 (100)|
        |*  1 |  TABLE ACCESS FULL | ORDERS |  200K |  5000   (1)|
        ---------------------------------------------------------

        Predicate Information (identified by operation id):
        ---------------------------------------------------
           1 - filter("STATUS"=:S)

        SQL_ID  abc123, child number 1
        -------------------------------------
        select * from orders where status = :s

        Plan hash value: 222
        ------------------------------------------------------------------
        | Id  | Operation                   | Name   | Rows  | Cost (%CPU)|
        ------------------------------------------------------------------
        |   0 | SELECT STATEMENT            |        |       |     3 (100)|
        |   1 |  TABLE ACCESS BY INDEX ROWID| ORDERS |     1 |     3   (0)|
        |*  2 |   INDEX RANGE SCAN          | IX_ST  |     1 |     2   (0)|
        ------------------------------------------------------------------

        Predicate Information (identified by operation id):
        ---------------------------------------------------
           2 - access("STATUS"=:S)
        """;

    [Fact]
    public void Each_child_cursor_is_parsed_as_its_own_plan()
    {
        var plans = PlanParser.ParseAll(TwoChildCursors);

        Assert.Equal(2, plans.Count);
        Assert.Equal("SQL_ID abc123, child 0", plans[0].Label);
        Assert.Equal(2, plans[0].Steps.Count);
        Assert.Equal("filter", Assert.Single(plans[0].Steps[1].Predicates).Type);

        Assert.Equal("SQL_ID abc123, child 1", plans[1].Label);
        Assert.Equal(3, plans[1].Steps.Count);
        Assert.Equal("access", Assert.Single(plans[1].Steps[2].Predicates).Type);
        Assert.Empty(plans[1].Steps[1].Predicates);
    }

    [Fact]
    public void Multi_plan_output_gives_one_report_per_plan()
    {
        var reports = new PlanAnalyzer().AnalyzeAll(TwoChildCursors, "cursor.txt");

        Assert.Equal(2, reports.Count);
        Assert.Equal("cursor.txt [SQL_ID abc123, child 0]", reports[0].DisplayName);
        Assert.Contains(reports[0].Findings, f => f.RuleId == "PX001");
        Assert.Empty(reports[1].Findings);
        Assert.StartsWith("3 plan steps", reports[1].Notes[0]);
    }

    [Fact]
    public void Two_plan_tables_without_separators_are_still_split()
    {
        const string text = """
            | Id  | Operation         | Name | Rows  | Cost (%CPU)|
            |   0 | SELECT STATEMENT  |      |     1 |     2   (0)|
            |   1 |  TABLE ACCESS FULL| A    |     1 |     2   (0)|
            | Id  | Operation         | Name | Rows  | Cost (%CPU)|
            |   0 | SELECT STATEMENT  |      |     1 |     2   (0)|
            |   1 |  TABLE ACCESS FULL| B    |     1 |     2   (0)|
            """;

        var plans = PlanParser.ParseAll(text);

        Assert.Equal(2, plans.Count);
        Assert.Equal("B", plans[1].Steps[1].ObjectName);
        Assert.Equal("plan 2", new PlanAnalyzer().AnalyzeAll(text, "p")[1].Section);
    }

    [Fact]
    public void Index_skip_scan_is_reported_as_info()
    {
        const string plan = """
            | Id  | Operation                   | Name        | Rows  | Cost (%CPU)|
            |   0 | SELECT STATEMENT            |             |    10 |    12   (0)|
            |   1 |  TABLE ACCESS BY INDEX ROWID| ORDERS      |    10 |    12   (0)|
            |*  2 |   INDEX SKIP SCAN           | IX_ORD_C_DT |    10 |     4   (0)|
            """;

        var finding = Assert.Single(new PlanAnalyzer().Analyze(plan, "p").Findings, f => f.RuleId == "PX006");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("IX_ORD_C_DT", finding.Message);
    }

    [Theory]
    [InlineData("| Id  | Operation | Name |")]
    [InlineData("| Id  | Operation | Name |\n|   0 | SELECT STAT")]
    [InlineData("| Id  | Operation | Name |\n|   x | SELECT STATEMENT |  |\n|     |                  |  |")]
    [InlineData("Predicate Information (identified by operation id):\n   1 - filter(\"A\"=1)")]
    public void Malformed_plans_never_throw(string text)
    {
        var report = new PlanAnalyzer().Analyze(text, "p");

        Assert.Empty(report.Findings);
        Assert.NotEmpty(report.Notes);
    }
}
