# PL/SQL Performance Doctor report

| Source | Type | Health | Critical | Warning | Info |
|---|---|---:|---:|---:|---:|
| `samples/order_processing_fixed.sql` | PL/SQL | 100/100 | 0 | 0 | 0 |
| `samples/order_processing_slow.sql` | PL/SQL | 11/100 | 5 | 9 | 2 |
| `samples/plans/daily_items_allstats.txt` | Execution plan | 82/100 | 0 | 2 | 0 |
| `samples/plans/revenue_by_city.txt` | Execution plan | 64/100 | 1 | 2 | 0 |

## `samples/order_processing_fixed.sql`

No performance problems found.

## `samples/order_processing_slow.sql`

### 🔵 PD004: SELECT * (line 9)

```sql
FOR o IN (SELECT * FROM orders WHERE status = 'NEW') LOOP
```

**Why:** SELECT * reads every column: more I/O, more network traffic and PGA memory, no index-only access, and the code silently changes behaviour when columns are added to the table.

**Fix:** List only the columns you actually use.

### 🟠 PD002: SELECT ... INTO inside a loop (N+1 queries) (line 11)

```sql
SELECT segment
```

**Why:** This SELECT ... INTO runs on every iteration of the loop that starts at line 9. With N driving rows Oracle parses, executes and fetches N extra queries (the N+1 pattern).

**Fix:** Join the lookup table into the loop's driving cursor, or load the lookup data once into an associative array (TABLE OF ... INDEX BY) before the loop.

<details><summary>Example</summary>

```sql
-- Join the lookup into the driving query instead of querying it per row
FOR r IN (SELECT o.order_id, c.segment
            FROM orders o
            JOIN customers c ON c.customer_id = o.customer_id
           WHERE o.status = 'NEW') LOOP
  ...
END LOOP;
```

</details>

### 🔴 PD001: Row-by-row DML inside a loop (line 18)

```sql
UPDATE orders
```

**Why:** UPDATE runs once per iteration of the loop that starts at line 9. Each execution is a separate PL/SQL-to-SQL context switch, so 1 million rows means 1 million statement executions.

**Fix:** Replace the loop with one set-based statement (INSERT ... SELECT, MERGE, or UPDATE with a join). If per-row PL/SQL logic is really needed, fetch with BULK COLLECT ... LIMIT and write with FORALL.

<details><summary>Example</summary>

```sql
-- Set-based: one statement, one context switch
INSERT INTO target_table (id, status, created_at)
SELECT s.id, s.status, SYSDATE
  FROM source_table s
 WHERE s.status = 'NEW';

-- Bulk: when per-row PL/SQL logic is really needed
OPEN c_source;
LOOP
  FETCH c_source BULK COLLECT INTO l_rows LIMIT 1000;
  EXIT WHEN l_rows.COUNT = 0;
  FORALL i IN 1 .. l_rows.COUNT
    INSERT INTO target_table VALUES l_rows(i);
END LOOP;
CLOSE c_source;
```

</details>

### 🔴 PD001: Row-by-row DML inside a loop (line 23)

```sql
INSERT INTO order_audit (order_id, old_status, new_status, changed_at)
```

**Why:** INSERT runs once per iteration of the loop that starts at line 9. Each execution is a separate PL/SQL-to-SQL context switch, so 1 million rows means 1 million statement executions.

**Fix:** Replace the loop with one set-based statement (INSERT ... SELECT, MERGE, or UPDATE with a join). If per-row PL/SQL logic is really needed, fetch with BULK COLLECT ... LIMIT and write with FORALL.

<details><summary>Example</summary>

```sql
-- Set-based: one statement, one context switch
INSERT INTO target_table (id, status, created_at)
SELECT s.id, s.status, SYSDATE
  FROM source_table s
 WHERE s.status = 'NEW';

-- Bulk: when per-row PL/SQL logic is really needed
OPEN c_source;
LOOP
  FETCH c_source BULK COLLECT INTO l_rows LIMIT 1000;
  EXIT WHEN l_rows.COUNT = 0;
  FORALL i IN 1 .. l_rows.COUNT
    INSERT INTO target_table VALUES l_rows(i);
END LOOP;
CLOSE c_source;
```

</details>

### 🟠 PD003: COMMIT inside a loop (line 26)

```sql
COMMIT;
```

**Why:** COMMIT inside the loop that starts at line 9 waits for a redo log write ('log file sync') on every iteration. It also makes ORA-01555 'snapshot too old' more likely on the driving cursor and leaves half-processed data if the job fails.

**Fix:** Commit once after the loop. For very large batches, commit per bulk chunk (for example every 10,000 rows) together with BULK COLLECT ... LIMIT, and make the job restartable.

### 🟠 PD010: WHEN OTHERS THEN NULL (line 29)

```sql
WHEN OTHERS THEN NULL;
```

**Why:** Every error is swallowed. Failed rows disappear without a trace, and performance problems such as timeouts, deadlocks or ORA-01555 become invisible.

**Fix:** Log the error (SQLCODE, SQLERRM, DBMS_UTILITY.FORMAT_ERROR_BACKTRACE) and re-raise it with RAISE. Only catch the specific exceptions you can actually handle.

### 🟠 PD005: Function applied to a column in a predicate (line 38)

```sql
WHERE TRUNC(order_date) = p_day;
```

**Why:** TRUNC(order_date) is evaluated for every row, so a normal index on order_date cannot be used and the optimizer usually falls back to a full table scan.

**Fix:** Rewrite it as a range on the raw column: order_date >= :day AND order_date < :day + 1. The optimizer can then use an index range scan on order_date.

### 🔵 PD011: COUNT(*) used as an existence check (line 45)

```sql
SELECT COUNT(*) INTO l_count
```

**Why:** COUNT(*) reads every matching row even though the code only needs to know whether one exists.

**Fix:** Stop at the first row: add AND ROWNUM = 1 to the WHERE clause, or use SELECT CASE WHEN EXISTS (SELECT 1 FROM ... WHERE ...) THEN 1 ELSE 0 END INTO l_found FROM dual.

### 🟠 PD005: Function applied to a column in a predicate (line 47)

```sql
WHERE UPPER(email) = UPPER(p_email);
```

**Why:** UPPER(email) is evaluated for every row, so a normal index on email cannot be used and the optimizer usually falls back to a full table scan.

**Fix:** Create a function-based index, e.g. CREATE INDEX ix_email_upper ON <table> (UPPER(email)); or store a normalized copy of the column.

### 🟠 PD006: LIKE with a leading wildcard (line 56)

```sql
WHERE full_name LIKE '%' || p_name || '%';
```

**Why:** LIKE '%' || ... starts with a wildcard, so an index on the column cannot be used for a range scan and every row has to be checked.

**Fix:** If users search by prefix, drop the leading %. For real 'contains' search, use an Oracle Text CONTEXT index and CONTAINS(column, :text) > 0.

### 🟠 PD009: BULK COLLECT without LIMIT (line 65)

```sql
FETCH c_old BULK COLLECT INTO l_ids;
```

**Why:** BULK COLLECT without LIMIT loads the entire result set into PGA memory in one go. On a large table this can exhaust session memory (ORA-04030) and slow down the whole server.

**Fix:** Fetch in chunks inside a loop with LIMIT 100 to 1000 and process each chunk with FORALL.

<details><summary>Example</summary>

```sql
OPEN c_old;
LOOP
  FETCH c_old BULK COLLECT INTO l_ids LIMIT 1000;
  EXIT WHEN l_ids.COUNT = 0;
  -- process this chunk with FORALL
END LOOP;
CLOSE c_old;
```

</details>

### 🔴 PD008: Dynamic SQL without bind variables (line 68)

```sql
EXECUTE IMMEDIATE 'DELETE FROM ' || p_table ||
```

**Why:** The SQL text is built by concatenating values instead of using bind variables. Every distinct value creates a new statement: a hard parse each time, shared pool and latch contention, and an SQL injection hole.

**Fix:** Put placeholders (:1, :name) in the SQL text and pass the values with USING. Identifiers such as table names cannot be bound, so validate them with DBMS_ASSERT.SQL_OBJECT_NAME or SIMPLE_SQL_NAME.

<details><summary>Example</summary>

```sql
-- Values are bound, identifiers are validated
EXECUTE IMMEDIATE
  'DELETE FROM ' || DBMS_ASSERT.SQL_OBJECT_NAME(p_table) ||
  ' WHERE order_date < :before'
  USING p_before;
```

</details>

### 🟠 PD007: NOT IN with a subquery (line 72)

```sql
WHERE customer_id NOT IN (SELECT customer_id FROM orders);
```

**Why:** NOT IN (SELECT ...) returns no rows at all if the subquery produces a single NULL. When the column is nullable Oracle also cannot always turn it into an efficient hash anti-join.

**Fix:** Use NOT EXISTS with a correlated subquery. It is NULL-safe and always eligible for an anti-join.

<details><summary>Example</summary>

```sql
DELETE FROM customers c
 WHERE NOT EXISTS (SELECT 1
                     FROM orders o
                    WHERE o.customer_id = c.customer_id);
```

</details>

### 🟠 PD012: Cursor loop nested inside a loop (line 80)

```sql
FOR l IN (SELECT quantity, unit_price FROM order_lines WHERE order_id = o.order_id) LOOP
```

**Why:** This cursor loop is nested inside the loop that starts at line 78, so its query is opened, executed and fetched once for every outer row. With N outer rows that is N extra queries, and the work inside the inner loop usually runs row by row as well.

**Fix:** Join the inner query into the outer cursor and process the combined result in one loop, or replace both loops with one set-based statement (INSERT ... SELECT, MERGE or UPDATE with a GROUP BY subquery).

<details><summary>Example</summary>

```sql
-- One query with a join instead of one inner query per outer row
FOR r IN (SELECT o.order_id, l.quantity, l.unit_price
            FROM orders o
            JOIN order_lines l ON l.order_id = o.order_id
           WHERE o.status = 'PROCESSED'
           ORDER BY o.order_id) LOOP
  ...
END LOOP;
```

</details>

### 🔴 PD001: Row-by-row DML inside a loop (line 84)

```sql
UPDATE orders SET total_amount = l_total WHERE order_id = o.order_id;
```

**Why:** UPDATE runs once per iteration of the loop that starts at line 78. Each execution is a separate PL/SQL-to-SQL context switch, so 1 million rows means 1 million statement executions.

**Fix:** Replace the loop with one set-based statement (INSERT ... SELECT, MERGE, or UPDATE with a join). If per-row PL/SQL logic is really needed, fetch with BULK COLLECT ... LIMIT and write with FORALL.

<details><summary>Example</summary>

```sql
-- Set-based: one statement, one context switch
INSERT INTO target_table (id, status, created_at)
SELECT s.id, s.status, SYSDATE
  FROM source_table s
 WHERE s.status = 'NEW';

-- Bulk: when per-row PL/SQL logic is really needed
OPEN c_source;
LOOP
  FETCH c_source BULK COLLECT INTO l_rows LIMIT 1000;
  EXIT WHEN l_rows.COUNT = 0;
  FORALL i IN 1 .. l_rows.COUNT
    INSERT INTO target_table VALUES l_rows(i);
END LOOP;
CLOSE c_source;
```

</details>

### 🔴 PD013: COMMIT inside a trigger (line 99)

```sql
COMMIT;
```

**Why:** The trigger commits in an autonomous transaction once for every row the triggering statement touches: one redo log sync ('log file sync') per row. The committed rows also survive when the main transaction rolls back, so they can describe changes that never happened.

**Fix:** Let the trigger write in the caller's transaction: remove the PRAGMA and the COMMIT. If the data really must survive a rollback, collect the rows in a compound trigger and write them once with FORALL in AFTER STATEMENT.

<details><summary>Example</summary>

```sql
-- Write in the caller's transaction: no PRAGMA, no COMMIT
CREATE OR REPLACE TRIGGER trg_orders_audit
AFTER UPDATE OF status ON orders
FOR EACH ROW
BEGIN
  INSERT INTO order_status_log (order_id, old_status, new_status, changed_at)
  VALUES (:OLD.order_id, :OLD.status, :NEW.status, SYSDATE);
END;
```

</details>


## `samples/plans/daily_items_allstats.txt`

- 6 plan steps, total buffer gets 258K.
- Hottest step: Id 4: INDEX UNIQUE SCAN PK_PRODUCTS (rows 85K, buffers 170K), 66% of the total buffer gets.
- Actual row counts found (ALLSTATS): estimate-vs-actual checks are enabled.

### 🟠 PX005: Nested loops over a large row source (step Id 2)

```text
Id 2: NESTED LOOPS (rows 85K, buffers 173K)
```

**Why:** The outer input (Id 3 ORDER_ITEMS) returns 85K rows, so the inner side (Id 4 PK_PRODUCTS) runs 85K times. The optimizer chose nested loops because it expected only 12 rows.

**Fix:** For large inputs a HASH JOIN is usually much faster. Fix the statistics first so the optimizer sees the real row count; use a USE_HASH hint only as a last resort.

### 🟠 PX004: Cardinality misestimate (step Id 3)

```text
Id 3: TABLE ACCESS FULL ORDER_ITEMS (rows 85K, buffers 3420)
```

**Why:** The optimizer expected 12 rows but got 85K. Every join method and join order above this step was chosen for the wrong data volume.

**Fix:** Gather fresh statistics on ORDER_ITEMS. If they are already fresh, the cause is usually correlated columns or skewed data: add extended statistics (DBMS_STATS.CREATE_EXTENDED_STATS) or a histogram.

<details><summary>Example</summary>

```sql
EXEC DBMS_STATS.GATHER_TABLE_STATS(ownname => USER, tabname => 'ORDER_ITEMS');
```

</details>


## `samples/plans/revenue_by_city.txt`

- 5 plan steps, total cost 4231.
- Hottest step: Id 4: TABLE ACCESS FULL ORDERS (rows 96K, cost 4190), 99% of the total cost.
- Tip: run the query with the /*+ GATHER_PLAN_STATISTICS */ hint and DBMS_XPLAN.DISPLAY_CURSOR(format => 'ALLSTATS LAST') to compare estimated and actual rows.

### 🔴 PX001: Expensive full table scan with a filter (step Id 4)

```text
Id 4: TABLE ACCESS FULL ORDERS (rows 96K, cost 4190)
```

**Why:** Oracle reads every block of ORDERS and discards non-matching rows with filter TRUNC(INTERNAL_FUNCTION("O"."ORDER_DATE"))>=TO_DATE(' 2026-01-01 00:00:00', 'syyyy-mm-dd hh24:mi:ss'). This step is 99% of the statement's cost.

**Fix:** The filtered column is wrapped in a function (see PX003): rewrite the predicate first, otherwise even a new index is ignored. Then index the filtered column(s) if the filter is selective: CREATE INDEX ix_orders_order_date ON orders (order_date); An index only pays off when the filter keeps a small part of the table (roughly under 5%), so check the selectivity first.

<details><summary>Example</summary>

```sql
CREATE INDEX ix_orders_order_date ON orders (order_date);
```

</details>

### 🟠 PX003: Column wrapped in a function in the predicate (step Id 3)

```text
Id 3: TABLE ACCESS FULL CUSTOMERS (rows 500, cost 37)
```

**Why:** The predicate applies UPPER() to column CITY, so a regular index on CITY cannot be used for this filter.

**Fix:** Create a function-based index on UPPER(city), or rewrite the predicate so the column stays bare.

### 🟠 PX003: Column wrapped in a function in the predicate (step Id 4)

```text
Id 4: TABLE ACCESS FULL ORDERS (rows 96K, cost 4190)
```

**Why:** Oracle applies INTERNAL_FUNCTION() to column ORDER_DATE. This is usually an implicit data type conversion (for example a VARCHAR2 column compared with a NUMBER, or a DATE compared with a TIMESTAMP), and it stops a normal index on the column from being used.

**Fix:** Compare the column with a value of exactly the same data type: convert the bind variable or literal, never the column.

