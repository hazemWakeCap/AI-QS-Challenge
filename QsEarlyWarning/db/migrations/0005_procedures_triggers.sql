-- 0005_procedures_triggers.sql — authorized-write procedures, cross-row validation, immutability
-- (plan §5.0 Choice 1; Findings 4 and 5 of the 3rd review; §9)
--
-- Two authorization ideas here:
--   * Snapshot columns are written ONLY by SECURITY DEFINER procedures owned by qs_owner. App/worker
--     hold no UPDATE privilege on those columns (0004), so "authorized transaction" is enforced by
--     PRIVILEGE, not by a flag a trigger trusts (Finding 4). The trigger is defense-in-depth: it
--     freezes closed-period facts against everyone, including the procedures.
--   * Completeness and plan-curve monotonicity are cross-row rules; CHECKs cannot express them, so
--     they are validated transactionally at period-close and publish (Finding 5). Failure raises with
--     a typed, enumerated list so the caller learns exactly what is missing/wrong.
--
-- Every routine here is SECURITY DEFINER with a fixed search_path and fully-qualified objects, and
-- re-checks the caller's membership (fn_is_member) so running as the owner cannot escalate privilege.

SET search_path = qs, public;

-- ── Reporting-currency immutability once monetary data exists (Finding 8) ──────
-- SECURITY INVOKER (runs as the caller): the monetary-data existence check must reflect what the
-- caller can see. An app updates its own project with context set (sees its baselines → blocks);
-- superuser/bypass see all rows anyway. RLS already prevents updating a project you cannot see, so
-- there is no path where a real currency change slips past this.
CREATE OR REPLACE FUNCTION qs.trg_projects_currency_immutable() RETURNS trigger
LANGUAGE plpgsql SET search_path = qs, pg_temp AS $$
BEGIN
    IF NEW.reporting_currency IS DISTINCT FROM OLD.reporting_currency THEN
        IF EXISTS (SELECT 1 FROM qs.cost_centre_baselines b WHERE b.project_id = OLD.id)
        OR EXISTS (SELECT 1 FROM qs.cost_centre_periods f WHERE f.project_id = OLD.id)
        OR EXISTS (SELECT 1 FROM qs.estimate_resource_lines r WHERE r.project_id = OLD.id) THEN
            RAISE EXCEPTION 'reporting_currency is immutable once monetary data exists (project %)', OLD.id
                USING ERRCODE = '23514';
        END IF;
    END IF;
    RETURN NEW;
END
$$;
ALTER FUNCTION qs.trg_projects_currency_immutable() OWNER TO qs_owner;
CREATE TRIGGER trg_projects_currency_immutable BEFORE UPDATE ON qs.projects
    FOR EACH ROW EXECUTE FUNCTION qs.trg_projects_currency_immutable();

-- ── Closed-period fact immutability (defense-in-depth; Choice 1 / Finding 4) ───
CREATE OR REPLACE FUNCTION qs.trg_freeze_closed_fact() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = qs, pg_temp AS $$
DECLARE v_status text;
BEGIN
    SELECT rp.status INTO v_status
      FROM qs.reporting_periods rp
     WHERE rp.project_id = OLD.project_id AND rp.id = OLD.reporting_period_id;
    IF v_status = 'closed' THEN
        RAISE EXCEPTION 'cost_centre_periods row % is in a closed period and is immutable', OLD.id
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$$;
ALTER FUNCTION qs.trg_freeze_closed_fact() OWNER TO qs_owner;
CREATE TRIGGER trg_freeze_closed_fact BEFORE UPDATE ON qs.cost_centre_periods
    FOR EACH ROW EXECUTE FUNCTION qs.trg_freeze_closed_fact();

-- ── Shared caller-authorization guard ──────────────────────────────────────────
CREATE OR REPLACE FUNCTION qs.fn_require_member(p_project_id bigint) RETURNS void
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = qs, pg_temp AS $$
BEGIN
    IF p_project_id IS DISTINCT FROM qs.fn_current_project_id() THEN
        RAISE EXCEPTION 'project % does not match the transaction project context', p_project_id
            USING ERRCODE = '42501';
    END IF;
    IF NOT qs.fn_is_member(p_project_id) THEN
        RAISE EXCEPTION 'caller is not a member of project %', p_project_id USING ERRCODE = '42501';
    END IF;
END
$$;
ALTER FUNCTION qs.fn_require_member(bigint) OWNER TO qs_owner;

-- ── period-open: snapshot bac/budget/planned_pct onto one fact per active centre ──
CREATE OR REPLACE PROCEDURE qs.sp_open_period(p_project_id bigint, p_reporting_period_id bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = qs, pg_temp AS $$
DECLARE
    v_version    bigint;
    v_period_ord int;
BEGIN
    PERFORM qs.fn_require_member(p_project_id);

    SELECT active_estimate_version_id INTO v_version FROM qs.projects WHERE id = p_project_id;
    IF v_version IS NULL THEN
        RAISE EXCEPTION 'project % has no active (published) estimate version', p_project_id USING ERRCODE = 'P0001';
    END IF;

    SELECT period_id INTO v_period_ord
      FROM qs.reporting_periods WHERE project_id = p_project_id AND id = p_reporting_period_id;
    IF v_period_ord IS NULL THEN
        RAISE EXCEPTION 'reporting period % not found in project %', p_reporting_period_id, p_project_id USING ERRCODE = 'P0001';
    END IF;

    UPDATE qs.reporting_periods
       SET status = 'open', opened_at = coalesce(opened_at, now())
     WHERE project_id = p_project_id AND id = p_reporting_period_id AND status <> 'closed';

    -- one fact per active centre for this period, snapshotting immutable baseline+plan numerics
    INSERT INTO qs.cost_centre_periods
        (project_id, cost_centre_id, reporting_period_id, baseline_id, estimate_version_id,
         bac_amount, budget_qty, planned_pct, lifecycle)
    SELECT p_project_id, cc.id, p_reporting_period_id, b.id, v_version,
           b.bac_amount, b.budget_qty, pp.planned_pct,
           CASE WHEN coalesce(pp.planned_pct,0) = 0 THEN 'NOT_STARTED' ELSE 'IN_PROGRESS' END
      FROM qs.cost_centres cc
      JOIN qs.cost_centre_baselines b
        ON b.project_id = p_project_id AND b.estimate_version_id = v_version AND b.cost_centre_id = cc.id
      LEFT JOIN qs.cost_centre_plan_periods pp
        ON pp.project_id = p_project_id AND pp.estimate_version_id = v_version
       AND pp.cost_centre_id = cc.id AND pp.reporting_period_id = p_reporting_period_id
     WHERE cc.project_id = p_project_id
       AND cc.effective_start_period <= v_period_ord
       AND (cc.effective_end_period IS NULL OR cc.effective_end_period >= v_period_ord)
    ON CONFLICT (project_id, cost_centre_id, reporting_period_id) DO NOTHING;

    UPDATE qs.projects SET data_revision = data_revision + 1 WHERE id = p_project_id;
END
$$;
ALTER PROCEDURE qs.sp_open_period(bigint, bigint) OWNER TO qs_owner;

-- ── rebaseline: re-snapshot the immutable numerics on OPEN periods only ────────
CREATE OR REPLACE PROCEDURE qs.sp_rebaseline_period(p_project_id bigint, p_reporting_period_id bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = qs, pg_temp AS $$
DECLARE v_version bigint;
BEGIN
    PERFORM qs.fn_require_member(p_project_id);
    IF (SELECT status FROM qs.reporting_periods WHERE project_id = p_project_id AND id = p_reporting_period_id) = 'closed' THEN
        RAISE EXCEPTION 'cannot rebaseline a closed period (%)', p_reporting_period_id USING ERRCODE = '23514';
    END IF;
    SELECT active_estimate_version_id INTO v_version FROM qs.projects WHERE id = p_project_id;

    UPDATE qs.cost_centre_periods f
       SET bac_amount = b.bac_amount,
           budget_qty = b.budget_qty,
           planned_pct = pp.planned_pct,
           baseline_id = b.id,
           estimate_version_id = v_version
      FROM qs.cost_centre_baselines b
      LEFT JOIN qs.cost_centre_plan_periods pp
        ON pp.project_id = p_project_id AND pp.estimate_version_id = v_version
       AND pp.cost_centre_id = b.cost_centre_id AND pp.reporting_period_id = p_reporting_period_id
     WHERE f.project_id = p_project_id AND f.reporting_period_id = p_reporting_period_id
       AND b.project_id = p_project_id AND b.estimate_version_id = v_version AND b.cost_centre_id = f.cost_centre_id;

    UPDATE qs.projects SET data_revision = data_revision + 1 WHERE id = p_project_id;
END
$$;
ALTER PROCEDURE qs.sp_rebaseline_period(bigint, bigint) OWNER TO qs_owner;

-- ── period-close completeness: which active centre-periods lack a fact? ────────
CREATE OR REPLACE FUNCTION qs.fn_validate_period_close(p_project_id bigint, p_reporting_period_id bigint)
RETURNS TABLE (cost_centre_id bigint, bcc_id text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = qs, pg_temp AS $$
    SELECT cc.id, cc.bcc_id
      FROM qs.cost_centres cc
      JOIN qs.reporting_periods rp
        ON rp.project_id = p_project_id AND rp.id = p_reporting_period_id
     WHERE cc.project_id = p_project_id
       AND cc.effective_start_period <= rp.period_id
       AND (cc.effective_end_period IS NULL OR cc.effective_end_period >= rp.period_id)
       AND NOT EXISTS (
           SELECT 1 FROM qs.cost_centre_periods f
            WHERE f.project_id = p_project_id
              AND f.cost_centre_id = cc.id
              AND f.reporting_period_id = p_reporting_period_id
       )
     ORDER BY cc.bcc_id
$$;
ALTER FUNCTION qs.fn_validate_period_close(bigint, bigint) OWNER TO qs_owner;

CREATE OR REPLACE PROCEDURE qs.sp_close_period(p_project_id bigint, p_reporting_period_id bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = qs, pg_temp AS $$
DECLARE v_missing int; v_list text;
BEGIN
    PERFORM qs.fn_require_member(p_project_id);
    SELECT count(*), string_agg(bcc_id, ', ' ORDER BY bcc_id)
      INTO v_missing, v_list
      FROM qs.fn_validate_period_close(p_project_id, p_reporting_period_id);
    IF v_missing > 0 THEN
        RAISE EXCEPTION 'period_close_incomplete: % active centre-period(s) missing a fact: %', v_missing, v_list
            USING ERRCODE = 'P0001';
    END IF;
    UPDATE qs.reporting_periods SET status = 'closed', closed_at = now()
     WHERE project_id = p_project_id AND id = p_reporting_period_id;
    UPDATE qs.projects SET data_revision = data_revision + 1 WHERE id = p_project_id;
END
$$;
ALTER PROCEDURE qs.sp_close_period(bigint, bigint) OWNER TO qs_owner;

-- ── publish validation: monotonic plan curve, coverage, rollup, horizon-100% rule ──
CREATE OR REPLACE FUNCTION qs.fn_validate_publish(p_project_id bigint, p_estimate_version_id bigint)
RETURNS TABLE (violation text, cost_centre_id bigint, detail text)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = qs, pg_temp AS $$
DECLARE v_horizon int;
BEGIN
    SELECT schedule_horizon_period_id INTO v_horizon
      FROM qs.estimate_versions WHERE project_id = p_project_id AND id = p_estimate_version_id;

    -- (a) active centres missing baseline coverage in this version
    RETURN QUERY
        SELECT 'missing_baseline'::text, cc.id, cc.bcc_id
          FROM qs.cost_centres cc
         WHERE cc.project_id = p_project_id
           AND NOT EXISTS (
               SELECT 1 FROM qs.cost_centre_baselines b
                WHERE b.project_id = p_project_id AND b.estimate_version_id = p_estimate_version_id
                  AND b.cost_centre_id = cc.id);

    -- (b) decreasing plan curve across consecutive planned periods (ordered by period ordinal)
    RETURN QUERY
        WITH curve AS (
            SELECT pp.cost_centre_id, rp.period_id, pp.planned_pct,
                   lag(pp.planned_pct) OVER (PARTITION BY pp.cost_centre_id ORDER BY rp.period_id) AS prev_pct
              FROM qs.cost_centre_plan_periods pp
              JOIN qs.reporting_periods rp ON rp.project_id = p_project_id AND rp.id = pp.reporting_period_id
             WHERE pp.project_id = p_project_id AND pp.estimate_version_id = p_estimate_version_id)
        SELECT 'decreasing_plan'::text, c.cost_centre_id,
               format('period %s: %s%% < previous %s%%', c.period_id, c.planned_pct, c.prev_pct)
          FROM curve c
         WHERE c.prev_pct IS NOT NULL AND c.planned_pct < c.prev_pct;

    -- (c) centre that must finish at 100% (marked complete, OR planned-finish within horizon)
    --     but whose plan curve does not reach 100 at its planned-finish period.
    RETURN QUERY
        SELECT 'plan_not_100_at_finish'::text, cc.id,
               format('planned_finish period %s expected 100%%', cc.planned_finish_period_id)
          FROM qs.cost_centres cc
         WHERE cc.project_id = p_project_id
           AND cc.planned_finish_period_id IS NOT NULL
           AND (cc.is_plan_complete OR (v_horizon IS NOT NULL AND cc.planned_finish_period_id <= v_horizon))
           AND NOT EXISTS (
               SELECT 1
                 FROM qs.cost_centre_plan_periods pp
                 JOIN qs.reporting_periods rp ON rp.project_id = p_project_id AND rp.id = pp.reporting_period_id
                WHERE pp.project_id = p_project_id AND pp.estimate_version_id = p_estimate_version_id
                  AND pp.cost_centre_id = cc.id AND rp.period_id = cc.planned_finish_period_id
                  AND pp.planned_pct = 100);

    -- (d) resource lines must roll up to boq_items.total_amount within tolerance (0.5% or 1.00)
    RETURN QUERY
        SELECT 'boq_rollup_mismatch'::text, NULL::bigint,
               format('boq_item %s: lines=%s vs total=%s', bi.id, r.line_sum, bi.total_amount)
          FROM qs.boq_items bi
          JOIN (SELECT boq_item_id, sum(resource_cost_amount) AS line_sum
                  FROM qs.estimate_resource_lines
                 WHERE project_id = p_project_id AND estimate_version_id = p_estimate_version_id
                 GROUP BY boq_item_id) r ON r.boq_item_id = bi.id
         WHERE bi.project_id = p_project_id AND bi.estimate_version_id = p_estimate_version_id
           AND bi.total_amount IS NOT NULL
           AND abs(r.line_sum - bi.total_amount) > greatest(1.00, 0.005 * bi.total_amount);
END
$$;
ALTER FUNCTION qs.fn_validate_publish(bigint, bigint) OWNER TO qs_owner;

CREATE OR REPLACE PROCEDURE qs.sp_publish_estimate_version(p_project_id bigint, p_estimate_version_id bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = qs, pg_temp AS $$
DECLARE v_n int; v_detail text;
BEGIN
    PERFORM qs.fn_require_member(p_project_id);
    SELECT count(*), string_agg(format('%s(%s): %s', violation, cost_centre_id, detail), ' | ')
      INTO v_n, v_detail
      FROM qs.fn_validate_publish(p_project_id, p_estimate_version_id);
    IF v_n > 0 THEN
        RAISE EXCEPTION 'publish_validation_failed: % issue(s): %', v_n, v_detail USING ERRCODE = 'P0001';
    END IF;

    -- supersede the currently-published version, publish this one, point the project at it.
    UPDATE qs.estimate_versions SET status = 'superseded'
     WHERE project_id = p_project_id AND status = 'published' AND id <> p_estimate_version_id;
    UPDATE qs.estimate_versions SET status = 'published', published_at = now()
     WHERE project_id = p_project_id AND id = p_estimate_version_id;
    UPDATE qs.projects SET active_estimate_version_id = p_estimate_version_id,
                           data_revision = data_revision + 1
     WHERE id = p_project_id;
END
$$;
ALTER PROCEDURE qs.sp_publish_estimate_version(bigint, bigint) OWNER TO qs_owner;

-- Let app/worker invoke the authorized-write procedures (they re-check membership internally).
GRANT EXECUTE ON FUNCTION  qs.fn_validate_period_close(bigint, bigint) TO qs_app, qs_worker;
GRANT EXECUTE ON FUNCTION  qs.fn_validate_publish(bigint, bigint)      TO qs_app, qs_worker;
GRANT EXECUTE ON PROCEDURE qs.sp_open_period(bigint, bigint)           TO qs_app, qs_worker;
GRANT EXECUTE ON PROCEDURE qs.sp_rebaseline_period(bigint, bigint)     TO qs_app, qs_worker;
GRANT EXECUTE ON PROCEDURE qs.sp_close_period(bigint, bigint)          TO qs_app, qs_worker;
GRANT EXECUTE ON PROCEDURE qs.sp_publish_estimate_version(bigint, bigint) TO qs_app, qs_worker;
