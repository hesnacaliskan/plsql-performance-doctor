-- The same package after applying PL/SQL Performance Doctor's suggestions.
CREATE OR REPLACE PACKAGE BODY order_processing AS

  PROCEDURE process_new_orders IS
  BEGIN
    -- Set-based: two statements in total instead of four statements per order.
    INSERT INTO order_audit (order_id, old_status, new_status, changed_at)
    SELECT order_id, 'NEW', 'PROCESSED', SYSDATE
      FROM orders
     WHERE status = 'NEW';

    MERGE INTO orders o
    USING (SELECT customer_id,
                  CASE segment WHEN 'GOLD' THEN 0.10 ELSE 0 END AS discount
             FROM customers) c
       ON (o.customer_id = c.customer_id)
     WHEN MATCHED THEN UPDATE
          SET o.discount = c.discount,
              o.status   = 'PROCESSED'
        WHERE o.status = 'NEW';

    COMMIT;
  EXCEPTION
    WHEN OTHERS THEN
      log_error('process_new_orders', SQLCODE, SQLERRM);
      RAISE;
  END process_new_orders;

  FUNCTION daily_revenue(p_day IN DATE) RETURN NUMBER IS
    l_total NUMBER;
  BEGIN
    -- A range on the bare column can use the index on order_date.
    SELECT SUM(amount)
      INTO l_total
      FROM orders
     WHERE order_date >= p_day
       AND order_date <  p_day + 1;
    RETURN l_total;
  END daily_revenue;

  FUNCTION customer_exists(p_email IN VARCHAR2) RETURN BOOLEAN IS
    l_count NUMBER;
  BEGIN
    SELECT COUNT(*) INTO l_count
      FROM customers
     -- plsql-doctor:ignore PD005 (covered by function-based index ix_customers_email_upper)
     WHERE UPPER(email) = UPPER(p_email)
       AND ROWNUM = 1;
    RETURN l_count > 0;
  END customer_exists;

  PROCEDURE search_customers(p_name IN VARCHAR2, p_result OUT SYS_REFCURSOR) IS
  BEGIN
    -- Oracle Text CONTEXT index instead of LIKE '%...%'.
    OPEN p_result FOR
      SELECT customer_id, full_name
        FROM customers
       WHERE CONTAINS(full_name, p_name) > 0;
  END search_customers;

  PROCEDURE archive_orders(p_table IN VARCHAR2, p_before IN DATE) IS
    TYPE t_ids IS TABLE OF orders.order_id%TYPE;
    l_ids t_ids;
    CURSOR c_old IS SELECT order_id FROM orders WHERE order_date < p_before;
  BEGIN
    OPEN c_old;
    LOOP
      FETCH c_old BULK COLLECT INTO l_ids LIMIT 1000;
      EXIT WHEN l_ids.COUNT = 0;
      FORALL i IN 1 .. l_ids.COUNT
        INSERT INTO orders_archive (order_id) VALUES (l_ids(i));
    END LOOP;
    CLOSE c_old;

    EXECUTE IMMEDIATE 'DELETE FROM ' || DBMS_ASSERT.SQL_OBJECT_NAME(p_table) ||
                      ' WHERE order_date < :before'
      USING p_before;

    DELETE FROM customers c
     WHERE NOT EXISTS (SELECT 1 FROM orders o WHERE o.customer_id = c.customer_id);
  END archive_orders;

  PROCEDURE recalculate_totals IS
  BEGIN
    -- One MERGE with an aggregate instead of one inner query and one UPDATE per order.
    MERGE INTO orders o
    USING (SELECT order_id, SUM(quantity * unit_price) AS total_amount
             FROM order_lines
            GROUP BY order_id) l
       ON (o.order_id = l.order_id)
     WHEN MATCHED THEN UPDATE
          SET o.total_amount = l.total_amount
        WHERE o.status = 'PROCESSED';
  END recalculate_totals;

END order_processing;
/

-- The audit row is written in the caller's transaction: no autonomous commit per row.
CREATE OR REPLACE TRIGGER trg_orders_audit
AFTER UPDATE OF status ON orders
FOR EACH ROW
BEGIN
  INSERT INTO order_status_log (order_id, old_status, new_status, changed_at)
  VALUES (:OLD.order_id, :OLD.status, :NEW.status, SYSDATE);
END;
/
