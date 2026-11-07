-- Nightly order processing job.
-- Intentionally slow: this is the kind of code PL/SQL Performance Doctor is built to catch.
CREATE OR REPLACE PACKAGE BODY order_processing AS

  PROCEDURE process_new_orders IS
    l_segment   customers.segment%TYPE;
    l_discount  NUMBER;
  BEGIN
    FOR o IN (SELECT * FROM orders WHERE status = 'NEW') LOOP

      SELECT segment
        INTO l_segment
        FROM customers
       WHERE customer_id = o.customer_id;

      l_discount := CASE l_segment WHEN 'GOLD' THEN 0.10 ELSE 0 END;

      UPDATE orders
         SET discount = l_discount,
             status   = 'PROCESSED'
       WHERE order_id = o.order_id;

      INSERT INTO order_audit (order_id, old_status, new_status, changed_at)
      VALUES (o.order_id, 'NEW', 'PROCESSED', SYSDATE);

      COMMIT;
    END LOOP;
  EXCEPTION
    WHEN OTHERS THEN NULL;
  END process_new_orders;

  FUNCTION daily_revenue(p_day IN DATE) RETURN NUMBER IS
    l_total NUMBER;
  BEGIN
    SELECT SUM(amount)
      INTO l_total
      FROM orders
     WHERE TRUNC(order_date) = p_day;
    RETURN l_total;
  END daily_revenue;

  FUNCTION customer_exists(p_email IN VARCHAR2) RETURN BOOLEAN IS
    l_count NUMBER;
  BEGIN
    SELECT COUNT(*) INTO l_count
      FROM customers
     WHERE UPPER(email) = UPPER(p_email);
    RETURN l_count > 0;
  END customer_exists;

  PROCEDURE search_customers(p_name IN VARCHAR2, p_result OUT SYS_REFCURSOR) IS
  BEGIN
    OPEN p_result FOR
      SELECT customer_id, full_name
        FROM customers
       WHERE full_name LIKE '%' || p_name || '%';
  END search_customers;

  PROCEDURE archive_orders(p_table IN VARCHAR2, p_before IN DATE) IS
    TYPE t_ids IS TABLE OF orders.order_id%TYPE;
    l_ids t_ids;
    CURSOR c_old IS SELECT order_id FROM orders WHERE order_date < p_before;
  BEGIN
    OPEN c_old;
    FETCH c_old BULK COLLECT INTO l_ids;
    CLOSE c_old;

    EXECUTE IMMEDIATE 'DELETE FROM ' || p_table ||
                      ' WHERE order_date < ''' || TO_CHAR(p_before, 'YYYY-MM-DD') || '''';

    DELETE FROM customers
     WHERE customer_id NOT IN (SELECT customer_id FROM orders);
  END archive_orders;

  PROCEDURE recalculate_totals IS
    l_total NUMBER;
  BEGIN
    FOR o IN (SELECT order_id FROM orders WHERE status = 'PROCESSED') LOOP
      l_total := 0;
      FOR l IN (SELECT quantity, unit_price FROM order_lines WHERE order_id = o.order_id) LOOP
        l_total := l_total + l.quantity * l.unit_price;
      END LOOP;

      UPDATE orders SET total_amount = l_total WHERE order_id = o.order_id;
    END LOOP;
  END recalculate_totals;

END order_processing;
/

CREATE OR REPLACE TRIGGER trg_orders_audit
AFTER UPDATE OF status ON orders
FOR EACH ROW
DECLARE
  PRAGMA AUTONOMOUS_TRANSACTION;
BEGIN
  INSERT INTO order_status_log (order_id, old_status, new_status, changed_at)
  VALUES (:OLD.order_id, :OLD.status, :NEW.status, SYSDATE);
  COMMIT;
END;
/
