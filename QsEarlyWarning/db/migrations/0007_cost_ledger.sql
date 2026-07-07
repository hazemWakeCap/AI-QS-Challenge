-- 0007_cost_ledger.sql — the append-only cost ledger, capture, and the one-time cutover
-- (plan §5.0 Choice 2, §7 Phase 3)
--
-- Before cutover, actual cost is the cumulative snapshot on the fact (import-compatible interim).
-- The cutover converts those cumulative snapshots into per-resource, per-period LEDGER deltas
-- (opening balance + increments), flips projects.ledger_active, and from then on:
--   * new actuals are POSTED to period_cost_deltas (append-only, idempotent);
--   * the fact ac_* cumulative columns are frozen (read-only trigger);
--   * cost_centre_evm reads the ledger-derived cumulative (the single writable AC source).

SET search_path = qs, public;

-- Append-only: app/worker may INSERT and SELECT deltas, never UPDATE/DELETE them.
REVOKE UPDATE, DELETE ON qs.period_cost_deltas FROM qs_app, qs_worker;

-- ── capture: post one cost delta (append-only, idempotent, open-period only) ───
CREATE OR REPLACE PROCEDURE qs.sp_post_cost_delta(
    p_project_id bigint, p_cost_centre_id bigint, p_reporting_period_id bigint,
    p_rtype text, p_amount numeric, p_direction text, p_idempotency_key text)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = qs, pg_temp AS $$
BEGIN
    PERFORM qs.fn_require_member(p_project_id);
    IF (SELECT status FROM qs.reporting_periods WHERE project_id = p_project_id AND id = p_reporting_period_id) = 'closed' THEN
        RAISE EXCEPTION 'cannot post cost to a closed period (%)', p_reporting_period_id USING ERRCODE = '23514';
    END IF;

    INSERT INTO qs.period_cost_deltas
        (project_id, cost_centre_id, reporting_period_id, rtype, amount, direction, idempotency_key)
    VALUES (p_project_id, p_cost_centre_id, p_reporting_period_id, p_rtype, p_amount, p_direction, p_idempotency_key)
    ON CONFLICT (project_id, idempotency_key) DO NOTHING;   -- idempotent re-submission

    UPDATE qs.projects SET data_revision = data_revision + 1 WHERE id = p_project_id;
END
$$;
ALTER PROCEDURE qs.sp_post_cost_delta(bigint, bigint, bigint, text, numeric, text, text) OWNER TO qs_owner;

-- ── one-time cutover: cumulative fact snapshots → per-resource ledger deltas ────
CREATE OR REPLACE PROCEDURE qs.sp_cutover_to_ledger(p_project_id bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = qs, pg_temp AS $$
BEGIN
    PERFORM qs.fn_require_member(p_project_id);
    IF (SELECT ledger_active FROM qs.projects WHERE id = p_project_id) THEN
        RAISE EXCEPTION 'project % is already on the ledger', p_project_id USING ERRCODE = 'P0001';
    END IF;

    -- Per (centre, resource type), the period-over-period increment of the cumulative snapshot
    -- (first period = full opening balance). Sign → POSTING / REVERSAL; skip zero increments.
    INSERT INTO qs.period_cost_deltas
        (project_id, cost_centre_id, reporting_period_id, rtype, amount, direction, idempotency_key)
    SELECT s.project_id, s.cost_centre_id, s.reporting_period_id, s.rtype,
           abs(s.delta),
           CASE WHEN s.delta >= 0 THEN 'POSTING' ELSE 'REVERSAL' END,
           format('cutover:%s:%s:%s', s.cost_centre_id, s.reporting_period_id, s.rtype)
    FROM (
        SELECT f.project_id, f.cost_centre_id, f.reporting_period_id, v.rtype,
               v.amt - lag(v.amt, 1, 0) OVER (PARTITION BY f.cost_centre_id, v.rtype ORDER BY rp.period_id) AS delta
        FROM qs.cost_centre_periods f
        JOIN qs.reporting_periods rp ON rp.project_id = f.project_id AND rp.id = f.reporting_period_id
        CROSS JOIN LATERAL (VALUES
            ('MANPOWER',    coalesce(f.ac_manpower_amount, 0)),
            ('MATERIAL',    coalesce(f.ac_material_amount, 0)),
            ('EQUIPMENT',   coalesce(f.ac_equipment_amount, 0)),
            ('SUBCONTRACT', coalesce(f.ac_subcontract_amount, 0))
        ) AS v(rtype, amt)
        WHERE f.project_id = p_project_id
    ) s
    WHERE s.delta <> 0;

    UPDATE qs.projects SET ledger_active = true, data_revision = data_revision + 1 WHERE id = p_project_id;
END
$$;
ALTER PROCEDURE qs.sp_cutover_to_ledger(bigint) OWNER TO qs_owner;

-- ── after cutover the fact ac_* cumulative columns are read-only (defense-in-depth) ──
CREATE OR REPLACE FUNCTION qs.trg_freeze_ledger_ac() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = qs, pg_temp AS $$
BEGIN
    IF (SELECT ledger_active FROM qs.projects WHERE id = OLD.project_id)
       AND (NEW.ac_material_amount    IS DISTINCT FROM OLD.ac_material_amount
         OR NEW.ac_manpower_amount    IS DISTINCT FROM OLD.ac_manpower_amount
         OR NEW.ac_equipment_amount   IS DISTINCT FROM OLD.ac_equipment_amount
         OR NEW.ac_subcontract_amount IS DISTINCT FROM OLD.ac_subcontract_amount) THEN
        RAISE EXCEPTION 'fact ac_* columns are frozen after the ledger cutover (project %); post to the ledger instead', OLD.project_id
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;
ALTER FUNCTION qs.trg_freeze_ledger_ac() OWNER TO qs_owner;
CREATE TRIGGER trg_freeze_ledger_ac BEFORE UPDATE ON qs.cost_centre_periods
    FOR EACH ROW EXECUTE FUNCTION qs.trg_freeze_ledger_ac();

GRANT EXECUTE ON PROCEDURE qs.sp_post_cost_delta(bigint, bigint, bigint, text, numeric, text, text) TO qs_app, qs_worker;
GRANT EXECUTE ON PROCEDURE qs.sp_cutover_to_ledger(bigint) TO qs_worker, qs_bypass;
