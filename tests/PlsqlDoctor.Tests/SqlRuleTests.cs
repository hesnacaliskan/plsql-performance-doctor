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
    [InlineData("BEGIN IF l_ok AND UPPER(status) = 'X' THEN NULL; END IF; END", false)]
    [InlineData("BEGIN IF a THEN NULL; ELSIF b OR TRIM(code) = 'Y' THEN NULL; END IF; END", false)]
    [InlineData("BEGIN LOOP EXIT WHEN done AND UPPER(state) = 'END'; END LOOP; END", false)]
    [InlineData("BEGIN l_match := found AND LOWER(name) = 'x'; END", false)]
    [InlineData("BEGIN IF l_ok THEN SELECT 1 INTO n FROM t WHERE a = 1 AND UPPER(name) = 'X'; END IF; END", true)]
    [InlineData("SELECT CASE WHEN x = 1 THEN 1 END FROM t WHERE a = 1 AND UPPER(name) = 'X'", true)]
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

    [Theory]
    [InlineData("SELECT id BULK COLLECT INTO l_ids FROM big_table", true)]
    [InlineData("SELECT id, name BULK COLLECT INTO l_ids, l_names FROM t WHERE x = 1", true)]
    [InlineData("SELECT id BULK COLLECT INTO l_ids FROM t FETCH FIRST 100 ROWS ONLY", false)]
    [InlineData("SELECT id BULK COLLECT INTO l_ids FROM t WHERE ROWNUM <= 100", false)]
    [InlineData("SELECT id INTO l_id FROM t WHERE x = 1", false)]
    [InlineData("DELETE FROM t WHERE x = 1 RETURNING id BULK COLLECT INTO l_ids", false)]
    [InlineData("INSERT INTO a SELECT id FROM (SELECT id FROM b)", false)]
    public void PD009_flags_select_bulk_collect_without_row_limit(string sql, bool expected)
    {
        Assert.Equal(expected, Ids($"BEGIN {sql}; END;").Contains("PD009"));
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

    [Theory]
    [InlineData("IF n > 0 THEN go; END IF;", true)]
    [InlineData("RETURN n = 0;", true)]
    [InlineData("IF 0 < n THEN go; END IF;", true)]
    [InlineData("l_total := n * 2;", false)]
    [InlineData("log_it('rows: ' || n);", false)]
    [InlineData("IF n > 100 THEN go; END IF;", false)]
    [InlineData("n := 5; log_it(n);", true)]
    public void PD011_ignores_counts_that_are_used_as_numbers(string usage, bool expected)
    {
        var sql = $"BEGIN SELECT COUNT(*) INTO n FROM t WHERE a = 1; {usage} END;";
        Assert.Equal(expected, Ids(sql).Contains("PD011"));
    }

    [Fact]
    public void PD011_usage_check_stops_at_the_next_subprogram()
    {
        const string sql = """
            FUNCTION a RETURN BOOLEAN IS n NUMBER;
            BEGIN SELECT COUNT(*) INTO n FROM t; RETURN n > 0; END a;
            PROCEDURE b IS n NUMBER;
            BEGIN n := 7; log_it(n * 2); END b;
            """;

        Assert.Contains("PD011", Ids(sql));
    }

    [Theory]
    [InlineData("EXECUTE IMMEDIATE 'SELECT * FROM t WHERE x = ' || TO_CHAR(5)", false)]
    [InlineData("EXECUTE IMMEDIATE 'BEGIN log_it(' || CHR(39) || 'x' || CHR(39) || '); END;'", false)]
    [InlineData("EXECUTE IMMEDIATE 'SELECT * FROM t WHERE x = ' || TO_CHAR(p_id)", true)]
    [InlineData("EXECUTE IMMEDIATE 'SELECT * FROM t WHERE d = ' || TO_CHAR(SYSDATE)", true)]
    [InlineData("EXECUTE IMMEDIATE 'SELECT * FROM t WHERE x = ' || my_pkg.format(5)", true)]
    public void PD008_builtin_functions_of_constants_are_not_values(string sql, bool expected)
    {
        Assert.Equal(expected, Ids($"BEGIN {sql}; END;").Contains("PD008"));
    }

    [Fact]
    public void PD012_flags_cursor_loop_inside_a_loop()
    {
        const string sql = """
            BEGIN
              FOR o IN (SELECT id FROM orders) LOOP
                FOR l IN (SELECT qty FROM lines WHERE order_id = o.id) LOOP
                  l_total := l_total + l.qty;
                END LOOP;
                FOR c IN c_items(o.id) LOOP
                  NULL;
                END LOOP;
              END LOOP;
            END;
            """;

        var findings = Analyze(sql).Where(f => f.RuleId == "PD012").ToList();
        Assert.Equal([3, 6], findings.Select(f => f.Line));
        Assert.Contains("line 2", findings[0].Message);
    }

    [Theory]
    [InlineData("FOR i IN 1 .. 10 LOOP NULL; END LOOP;")]
    [InlineData("FOR i IN REVERSE 1 .. n LOOP NULL; END LOOP;")]
    [InlineData("FOR i IN l_ids.FIRST .. l_ids.LAST LOOP NULL; END LOOP;")]
    [InlineData("FOR i IN INDICES OF l_ids LOOP NULL; END LOOP;")]
    [InlineData("FORALL i IN 1 .. l_ids.COUNT DELETE FROM t WHERE id = l_ids(i);")]
    public void PD012_ignores_numeric_and_collection_loops(string inner)
    {
        var sql = $"BEGIN FOR o IN (SELECT id FROM orders) LOOP {inner} END LOOP; END;";
        Assert.DoesNotContain("PD012", Ids(sql));
    }

    [Fact]
    public void PD012_ignores_a_single_cursor_loop()
    {
        Assert.DoesNotContain("PD012", Ids("BEGIN FOR o IN (SELECT id FROM orders) LOOP NULL; END LOOP; END;"));
    }

    [Fact]
    public void PD013_flags_commit_in_row_level_autonomous_trigger()
    {
        const string sql = """
            CREATE OR REPLACE TRIGGER trg_audit
            AFTER UPDATE ON orders
            FOR EACH ROW
            DECLARE
              PRAGMA AUTONOMOUS_TRANSACTION;
            BEGIN
              INSERT INTO audit_log (id) VALUES (:NEW.id);
              COMMIT;
            END;
            /
            CREATE OR REPLACE PROCEDURE p IS
            BEGIN
              UPDATE t SET x = 1;
              COMMIT;
            END;
            /
            """;

        var finding = Assert.Single(Analyze(sql), f => f.RuleId == "PD013");
        Assert.Equal(8, finding.Line);
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains("every row", finding.Message);
    }

    [Fact]
    public void PD013_severity_depends_on_trigger_kind()
    {
        const string statementLevel = """
            CREATE TRIGGER trg_log AFTER INSERT ON orders
            DECLARE PRAGMA AUTONOMOUS_TRANSACTION;
            BEGIN INSERT INTO load_log VALUES (SYSDATE); COMMIT; END;
            """;
        const string notAutonomous = "CREATE TRIGGER trg_x BEFORE INSERT ON t BEGIN COMMIT; END;";

        Assert.Equal(Severity.Warning, Assert.Single(Analyze(statementLevel), f => f.RuleId == "PD013").Severity);
        var invalid = Assert.Single(Analyze(notAutonomous), f => f.RuleId == "PD013");
        Assert.Equal(Severity.Critical, invalid.Severity);
        Assert.Contains("ORA-04092", invalid.Message);
    }

    [Theory]
    [InlineData("BEGIN\n  l := 'abc;\n")]
    [InlineData("BEGIN\n  /* never closed\n  COMMIT;")]
    [InlineData("q'[unterminated")]
    [InlineData("\"unterminated identifier")]
    [InlineData("END LOOP; END LOOP; LOOP")]
    [InlineData("")]
    public void Malformed_source_never_throws(string sql)
    {
        var report = new SqlAnalyzer().Analyze(sql, "bad.sql");
        Assert.NotNull(report.Findings);
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

    [Fact]
    public void Ignore_comment_skips_blank_and_comment_lines_before_the_code()
    {
        const string sql = """
            BEGIN
              FOR r IN (SELECT id FROM t) LOOP
                -- plsql-doctor:ignore PD001 (tiny lookup table, at most 20 rows)

                -- see ticket DBA-1234
                INSERT INTO a VALUES (r.id);
                INSERT INTO b VALUES (r.id);
              END LOOP;
            END;
            """;

        var finding = Assert.Single(Analyze(sql));
        Assert.Equal(7, finding.Line);
    }
}
