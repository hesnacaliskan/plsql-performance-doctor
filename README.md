# PL/SQL Performance Doctor

[![CI](https://github.com/OWNER/plsql-performance-doctor/actions/workflows/ci.yml/badge.svg)](https://github.com/OWNER/plsql-performance-doctor/actions/workflows/ci.yml)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

**A linter for slow Oracle code.** It reads PL/SQL source and `DBMS_XPLAN` execution plans, finds the
patterns that make batch jobs run for hours, explains *why* each one is slow, and shows the fix.

```text
[CRITICAL] PD001  Row-by-row DML inside a loop  line 23
           | INSERT INTO order_audit (order_id, old_status, new_status, changed_at)
           Why  INSERT runs once per iteration of the loop that starts at line 9. Each
                execution is a separate PL/SQL-to-SQL context switch, so 1 million rows
                means 1 million statement executions.
           Fix  Replace the loop with one set-based statement (INSERT ... SELECT, MERGE, or
                UPDATE with a join). If per-row PL/SQL logic is really needed, fetch with
                BULK COLLECT ... LIMIT and write with FORALL.

[CRITICAL] PX001  Expensive full table scan with a filter  step Id 4
           | Id 4: TABLE ACCESS FULL ORDERS (rows 96K, cost 4190)
           Why  Oracle reads every block of ORDERS and discards non-matching rows with
                filter TRUNC(INTERNAL_FUNCTION("O"."ORDER_DATE"))>=... This step is 99% of
                the statement's cost.
```

## Why this exists

Banks, insurers and manufacturers still run their core business on Oracle PL/SQL: nightly batches,
interest calculations, data integration packages. The same handful of mistakes shows up again and again:

- a cursor loop that does `INSERT`/`UPDATE` row by row,
- a lookup `SELECT ... INTO` inside that loop,
- `COMMIT` on every iteration,
- `WHERE TRUNC(order_date) = :d` that silently disables the index,
- dynamic SQL glued together with `||` instead of bind variables.

None of them are syntax errors, so they pass code review and only show up in production, when the job
that took 10 minutes on test data takes 6 hours on real data. Generic AI code assistants don't know your
execution plan, and general SQL linters care about style, not performance.

PL/SQL Performance Doctor focuses on **performance only** and is designed to run **where code review
happens**: in the terminal and in CI, with no database connection needed.

## Features

- **13 PL/SQL rules.** It detects loop structure, so it knows the difference between a query that runs
  once and one that runs once per row.
- **6 execution plan rules.** It parses `DBMS_XPLAN.DISPLAY` and `DISPLAY_CURSOR` output, including
  `ALLSTATS LAST` with estimated vs. actual rows. Output with several child cursors is split into one
  report per plan.
- **Concrete fixes.** Findings include rewritten code or ready-to-run DDL, for example
  `CREATE INDEX ix_orders_order_date ON orders (order_date);`.
- **Health score** from 0 to 100 per file.
- **CI friendly.** Console, Markdown, JSON and SARIF output, `--fail-on` exit codes, and inline
  suppression comments. SARIF puts findings inline on GitHub pull requests through code scanning.
- **No false positives from comments or strings.** It uses a real PL/SQL lexer, including `q'[...]'`
  quoting, not regex on raw text.
- **Legacy encodings.** Files without a BOM that are not valid UTF-8 are read with the system ANSI code
  page, or with the one you name: `--encoding windows-1254`.

## Quick start

```bash
git clone https://github.com/OWNER/plsql-performance-doctor.git
cd plsql-performance-doctor
dotnet run --project src/PlsqlDoctor.Cli -- analyze samples
```

Analyze your own code or a plan:

```bash
# A file or a whole folder of .sql / .pks / .pkb / .prc / .fnc / .trg files
dotnet run --project src/PlsqlDoctor.Cli -- analyze path/to/packages

# An execution plan saved from SQL Developer, SQL*Plus or Toad
dotnet run --project src/PlsqlDoctor.Cli -- plan my_plan.txt --examples

# Pipe a plan in
sqlplus -s user/pass @show_plan.sql | dotnet run --project src/PlsqlDoctor.Cli -- plan -
```

Install it as a global tool:

```bash
dotnet pack src/PlsqlDoctor.Cli -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg PlsqlPerformanceDoctor
plsql-doctor analyze .
```

### Getting a good plan

Estimated plans are useful, but plans with actual row counts are much better:

```sql
SELECT /*+ GATHER_PLAN_STATISTICS */ ... ;   -- run the real query once

SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR(format => 'ALLSTATS LAST'));
```

With `A-Rows` present, the doctor can point at the exact step where the optimizer's estimate went wrong.

## Rules

| Id | Severity | Detects |
|---|---|---|
| PD001 | Critical | `INSERT`/`UPDATE`/`DELETE`/`MERGE` inside a loop (row-by-row processing) |
| PD002 | Warning | `SELECT ... INTO` inside a loop (N+1 queries) |
| PD003 | Warning | `COMMIT` inside a loop |
| PD004 | Info | `SELECT *` |
| PD005 | Warning | Function on a column in a predicate: `UPPER(col) =`, `TRUNC(date_col) =` |
| PD006 | Warning | `LIKE '%...'` with a leading wildcard |
| PD007 | Warning | `NOT IN (SELECT ...)`, which is NULL-unsafe and can block anti-joins |
| PD008 | Critical | `EXECUTE IMMEDIATE` built with `\|\|` instead of bind variables |
| PD009 | Warning | `FETCH ... BULK COLLECT` without `LIMIT`, `SELECT ... BULK COLLECT` without a row limit |
| PD010 | Warning | `WHEN OTHERS THEN NULL` |
| PD011 | Info | `SELECT COUNT(*) INTO` used only as an existence check |
| PD012 | Warning | Cursor `FOR` loop nested inside another loop |
| PD013 | Critical/Warning | `COMMIT` inside a trigger, usually an autonomous transaction per row |
| PX001 | Warning/Critical | Expensive `TABLE ACCESS FULL` that throws rows away with a filter |
| PX002 | Critical | `MERGE JOIN CARTESIAN` |
| PX003 | Warning | Column wrapped in a function or an implicit conversion (`INTERNAL_FUNCTION`, `TO_NUMBER`) |
| PX004 | Warning | Cardinality misestimate (`E-Rows` vs. `A-Rows` off by 10x or more) |
| PX005 | Warning | `NESTED LOOPS` driven by a large row source |
| PX006 | Info | `INDEX SKIP SCAN` |

Run `plsql-doctor rules` to list them from the tool.

## Before and after

[`samples/order_processing_slow.sql`](samples/order_processing_slow.sql) is a typical nightly job that
triggers every PL/SQL rule. [`samples/order_processing_fixed.sql`](samples/order_processing_fixed.sql)
is the same package after applying the suggestions.

| File | Health | Critical | Warning | Info |
|---|---:|---:|---:|---:|
| `order_processing_slow.sql` | 11/100 | 5 | 9 | 2 |
| `order_processing_fixed.sql` | 100/100 | 0 | 0 | 0 |

The full Markdown report is in [docs/sample-report.md](docs/sample-report.md).

## Use it in CI

```yaml
- name: PL/SQL performance check
  run: |
    dotnet run --project tools/plsql-performance-doctor/src/PlsqlDoctor.Cli -- \
      analyze db/ --format markdown --out plsql-report.md --fail-on critical
    cat plsql-report.md >> "$GITHUB_STEP_SUMMARY"
```

| Exit code | Meaning |
|---|---|
| 0 | No finding at or above `--fail-on` (default `critical`) |
| 1 | At least one finding at or above `--fail-on` |
| 2 | Usage error |

To see findings inline on pull requests, upload SARIF to GitHub code scanning:

```yaml
permissions:
  security-events: write

steps:
  - name: PL/SQL performance check
    run: |
      dotnet run --project tools/plsql-performance-doctor/src/PlsqlDoctor.Cli -- \
        analyze db/ --format sarif --out plsql-doctor.sarif --fail-on none
  - uses: github/codeql-action/upload-sarif@v3
    with:
      sarif_file: plsql-doctor.sarif
      category: plsql-performance-doctor
```

When a finding is intentional, suppress it on the same line or above the code and say why. Blank lines
and other comment lines between the suppression and the code are allowed:

```sql
-- plsql-doctor:ignore PD005 (covered by function-based index ix_customers_email_upper)
WHERE UPPER(email) = UPPER(p_email)
```

## How it works

```text
             +----------------+     +-------------------+     +-------------+
 .sql  --->  |  PL/SQL lexer  | --> | SqlContext        | --> | PD rules    | --+
             | (comments,     |     | loop depth, loop  |     | PD001-PD013 |   |
             |  strings, q'') |     | start, statements |     +-------------+   |
             +----------------+     +-------------------+                       |    +-----------+
                                                                                +--> | Reporters | console / md / json / sarif
             +----------------+     +-------------------+     +-------------+   |    +-----------+
 xplan --->  |  Plan parser   | --> | Plan tree         | --> | PX rules    | --+
             | (table, preds) |     | cost, rows, A-Rows|     | PX001-PX006 |
             +----------------+     +-------------------+     +-------------+
```

- `src/PlsqlDoctor.Core` contains the lexer, the rules, the plan parser and the reporters. It has no
  dependencies.
- `src/PlsqlDoctor.Cli` is the `plsql-doctor` command.
- `tests/PlsqlDoctor.Tests` has xUnit tests for every rule, both positive and negative cases.

Adding a rule means implementing `ISqlRule` or `IPlanRule`, registering it in `DefaultRules`, and adding
tests.

## Limitations

The doctor reads one file at a time and does not connect to a database, so some things are out of reach:

- **No analysis across procedures.** A loop that calls `log_pkg.write_row(r.id)` is not flagged even if
  that procedure does an `INSERT`, because the body of the called procedure is not followed.
- **No data dictionary.** The doctor does not know table sizes, existing indexes or column types. A full
  scan of a small table or a `SELECT ... BULK COLLECT` from a 20-row lookup table can be fine; suppress
  those findings with a comment.
- **Heuristics, not a full parser.** Rules work on tokens and loop structure. Unusual formatting can
  still cause a missed finding or a false positive. Please open an issue with the snippet.
- **Plans are only as good as their statistics.** Estimated plans show what the optimizer believes. Use
  `ALLSTATS LAST` to see what really happened.

## Roadmap

- [x] **v0.1** Static PL/SQL analysis, plan analysis, CLI, CI.
- [ ] **v0.2 Live mode.** Connect to Oracle with ODP.NET, run `EXPLAIN PLAN` or `DISPLAY_CURSOR` by
      `SQL_ID`, and read the worst statements from `V$SQL`.
- [ ] **v0.3 Before/after benchmark.** Run the original and the rewritten statement and compare elapsed
      time, buffer gets and executions. Ship a Docker Compose file with Oracle Database Free and a demo
      schema.
- [ ] **v0.4 AI explain.** An LLM explains findings in the context of your code and drafts a rewrite. The
      draft is checked again by the rule engine, so the AI never has the last word.
- [ ] **v0.5** Web UI (ASP.NET Core): paste code or a plan and get the report in the browser.

## License

[MIT](LICENSE)
