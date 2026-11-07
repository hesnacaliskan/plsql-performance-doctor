using PlsqlDoctor.Core.Findings;
using PlsqlDoctor.Core.PlSql;

namespace PlsqlDoctor.Tests;

public class SqlRuleTests
{
    private static IReadOnlyList<Finding> Analyze(string sql) => new SqlAnalyzer().Analyze(sql, "test.sql").Findings;

    private static List<string> Ids(string sql) => Analyze(sql).Select(f => f.RuleId).ToList();

    [Fact]
    public void PD001_flags_dml_in_cursor_for_loop_and_points_to_the_loop()
    {
        const string sql = """
            BEGIN
              FOR r IN (SELECT id FROM src WHERE x IS NULL)
              LOOP
                INSERT INTO dst (id) VALUES (r.id);
              END LOOP;
            END;
            """;

        var finding = Assert.Single(Analyze(sql), f => f.RuleId == "PD001");
        Assert.Equal(4, finding.Line);
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains("line 2", finding.Message);
    }

    [Fact]
    public void PD001_ignores_forall_collection_delete_and_for_update()
    {
        const string sql = """
            BEGIN
              LOOP
                FETCH c BULK COLLECT INTO l_ids LIMIT 100;
                EXIT WHEN l_ids.COUNT = 0;
                FORALL i IN 1 .. l_ids.COUNT
                  UPDATE t SET flag = 1 WHERE id = l_ids(i);
                l_ids.DELETE;
              END LOOP;
              FOR r IN (SELECT id FROM t FOR UPDATE) LOOP
                NULL;
              END LOOP;
            END;
            """;

        Assert.DoesNotContain("PD001", Ids(sql));
    }

    [Fact]
    public void PD001_does_not_flag_dml_outside_loops()
    {
        Assert.Empty(Ids("BEGIN UPDATE t SET x = 1; DELETE FROM t WHERE y = 2; END;"));
    }

    [Fact]
    public void PD002_flags_select_into_in_loop_but_not_the_driving_query()
    {
        const string sql = """
            BEGIN
              FOR r IN (SELECT id FROM orders) LOOP
                SELECT name INTO l_name FROM customers WHERE id = r.id;
              END LOOP;
            END;
            """;

        var finding = Assert.Single(Analyze(sql), f => f.RuleId == "PD002");
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void PD003_flags_commit_inside_loop_only()
    {
        const string sql = """
            BEGIN
              WHILE l_more LOOP
                COMMIT;
              END LOOP;
              COMMIT;
            END;
            """;

        var finding = Assert.Single(Analyze(sql), f => f.RuleId == "PD003");
        Assert.Equal(3, finding.Line);
    }

    [Theory]
    [InlineData("SELECT * FROM t", true)]
    [InlineData("SELECT COUNT(*) FROM t", false)]
    [InlineData("SELECT id FROM t WHERE EXISTS (SELECT * FROM u WHERE u.id = t.id)", false)]
    public void PD004_select_star(string sql, bool expected)
    {
        Assert.Equal(expected, Ids(sql + ";").Contains("PD004"));
    }

    [Theory]
    [InlineData("SELECT 1 FROM t WHERE TRUNC(created_at) = p_day", true)]
    [InlineData("SELECT 1 FROM t WHERE x = 1 AND UPPER(c.email) = :e", true)]
    [InlineData("SELECT 1 FROM t WHERE email = UPPER(p_email)", false)]
    [InlineData("SELECT 1 FROM t WHERE UPPER(p_email) = email", false)]
    [InlineData("SELECT 1 FROM t WHERE TRUNC(SYSDATE) = d", false)]
    [InlineData("SELECT UPPER(name) FROM t", false)]
    public void PD005_function_on_column(string sql, bool expected)
    {
        Assert.Equal(expected, Ids(sql + ";").Contains("PD005"));
    }

    [Theory]
    [InlineData("SELECT 1 FROM t WHERE name LIKE '%ali%'", true)]
    [InlineData("SELECT 1 FROM t WHERE name LIKE '%' || p_name", true)]
    [InlineData("SELECT 1 FROM t WHERE name LIKE 'ali%'", false)]
    public void PD006_leading_wildcard(string sql, bool expected)
    {
        Assert.Equal(expected, Ids(sql + ";").Contains("PD006"));
    }

    [Fact]
    public void PD007_flags_not_in_subquery_but_not_value_lists()
    {
        Assert.Contains("PD007", Ids("DELETE FROM a WHERE id NOT IN (SELECT id FROM b);"));
        Assert.DoesNotContain("PD007", Ids("DELETE FROM a WHERE id NOT IN (1, 2, 3);"));
    }

    [Fact]
    public void PD008_flags_concatenated_values()
    {
        Assert.Contains("PD008", Ids("BEGIN EXECUTE IMMEDIATE 'DELETE FROM t WHERE id = ' || p_id; END;"));
    }

    [Fact]
    public void PD008_flags_variable_built_by_concatenation()
    {
        const string sql = """
            BEGIN
              l_sql := 'UPDATE t SET x = ' || p_value;
              EXECUTE IMMEDIATE l_sql;
            END;
            """;

        Assert.Contains("PD008", Ids(sql));
    }

    [Fact]
    public void PD008_accepts_bind_variables_and_dbms_assert()
    {
        const string sql = """
            BEGIN
              EXECUTE IMMEDIATE 'DELETE FROM t WHERE id = :1' USING p_id;
              EXECUTE IMMEDIATE 'TRUNCATE TABLE ' || DBMS_ASSERT.SQL_OBJECT_NAME(p_table);
            END;
            """;

        Assert.DoesNotContain("PD008", Ids(sql));
    }

    [Fact]
    public void PD009_flags_bulk_collect_without_limit()
    {
        Assert.Contains("PD009", Ids("BEGIN FETCH c BULK COLLECT INTO l_rows; END;"));
        Assert.DoesNotContain("PD009", Ids("BEGIN FETCH c BULK COLLECT INTO l_rows LIMIT 500; END;"));
    }

    [Fact]
    public void PD010_flags_when_others_then_null()
    {
        Assert.Contains("PD010", Ids("BEGIN NULL; EXCEPTION WHEN OTHERS THEN NULL; END;"));
        Assert.DoesNotContain("PD010", Ids("BEGIN NULL; EXCEPTION WHEN OTHERS THEN log_it; RAISE; END;"));
    }

    [Fact]
    public void PD011_flags_count_for_existence_unless_limited()
    {
        Assert.Contains("PD011", Ids("BEGIN SELECT COUNT(*) INTO n FROM t WHERE a = 1; END;"));
        Assert.DoesNotContain("PD011", Ids("BEGIN SELECT COUNT(*) INTO n FROM t WHERE a = 1 AND ROWNUM = 1; END;"));
    }

    [Fact]
    public void Keywords_inside_comments_and_strings_never_trigger_rules()
    {
        const string sql = """
            BEGIN
              FOR r IN (SELECT id FROM t) LOOP
                -- COMMIT; INSERT INTO x VALUES (1);
                log_it('UPDATE orders SET x = 1; COMMIT;');
              END LOOP;
            END;
            """;

        Assert.Empty(Ids(sql));
    }

    [Fact]
    public void Ignore_comment_suppresses_finding_on_the_next_line()
    {
        const string sql = """
            SELECT 1 FROM t
            -- plsql-doctor:ignore PD005 (function-based index exists)
            WHERE UPPER(email) = :e;
            SELECT 1 FROM t WHERE UPPER(email) = :e;
            """;

        var finding = Assert.Single(Analyze(sql));
        Assert.Equal(4, finding.Line);
    }
}
