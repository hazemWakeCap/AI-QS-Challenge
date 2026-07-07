-- 0004_rls.sql — row-level security, non-recursive membership, and privilege grants
-- (plan §5.0 Choice 4; Findings 5/6/10, 3r-1, and 2–3 of the 3rd review)
--
-- The boundary rests on three things working together:
--   1. Transaction-local identity: TWO settings, app.current_user_id AND app.current_project_id,
--      each validated by the app BEFORE the transaction opens. A project id alone is never trusted.
--   2. A non-recursive membership lookup: fn_is_member is SECURITY DEFINER with a fixed search_path;
--      project_memberships carries a policy keyed ONLY on the current user id (no table reference),
--      so evaluating the tenant policy (which calls fn_is_member) can never recurse back into a
--      table whose own policy calls fn_is_member.
--   3. FORCE RLS on every tenant table for the owner too, plus app/worker roles that hold no
--      BYPASSRLS and own nothing.

SET search_path = qs, public;

-- ── Transaction-local identity readers (GUC-only; no table access → safe in any policy) ──
CREATE OR REPLACE FUNCTION qs.fn_current_user_id() RETURNS bigint
LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('app.current_user_id', true), '')::bigint
$$;

CREATE OR REPLACE FUNCTION qs.fn_current_project_id() RETURNS bigint
LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('app.current_project_id', true), '')::bigint
$$;

-- ── Owner-safe membership resolution (SECURITY DEFINER, fixed search_path) ──────
-- Returns true iff the current user (from the GUC) is a member of p_project_id. Because
-- project_memberships' own policy is keyed on fn_current_user_id(), this reads only the caller's
-- own membership rows even under FORCE RLS — no BYPASSRLS needed, and no recursion.
CREATE OR REPLACE FUNCTION qs.fn_is_member(p_project_id bigint) RETURNS boolean
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = qs, pg_temp AS $$
    SELECT p_project_id IS NOT NULL
       AND qs.fn_current_user_id() IS NOT NULL
       AND EXISTS (
           SELECT 1 FROM qs.project_memberships m
           WHERE m.project_id = p_project_id
             AND m.user_id = qs.fn_current_user_id()
       )
$$;
ALTER FUNCTION qs.fn_is_member(bigint) OWNER TO qs_owner;
REVOKE ALL ON FUNCTION qs.fn_is_member(bigint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION qs.fn_is_member(bigint) TO qs_app, qs_worker;

-- ── Schema + object privileges (RLS still governs every row) ───────────────────
GRANT USAGE ON SCHEMA qs TO qs_app, qs_worker, qs_bypass;

-- Broad DML on the estimate-graph + calendar + master tables (RLS scopes it to the project).
GRANT SELECT, INSERT, UPDATE, DELETE ON
    qs.projects, qs.project_memberships, qs.reporting_periods, qs.estimate_versions,
    qs.norms, qs.norm_materials, qs.estimate_packages, qs.boq_items, qs.boq_norm_mappings,
    qs.estimate_resource_lines, qs.cost_centres, qs.cost_centre_baselines,
    qs.cost_centre_plan_periods, qs.period_cost_deltas
TO qs_app, qs_worker;

GRANT SELECT ON qs.cost_centre_evm TO qs_app, qs_worker;

-- The fact table is special: the snapshot columns are NOT app-writable. App/worker may read facts
-- and update ONLY the actual-input columns. Fact creation happens through the period-open
-- procedure (0005), so no direct INSERT/DELETE either.
GRANT SELECT ON qs.cost_centre_periods TO qs_app, qs_worker;
GRANT UPDATE (actual_pct_complete, ac_material_amount, ac_manpower_amount,
              ac_equipment_amount, ac_subcontract_amount, lifecycle)
    ON qs.cost_centre_periods TO qs_app, qs_worker;

-- The bypass role (migrations / purge / backfill only) gets everything and skips RLS via BYPASSRLS.
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA qs TO qs_bypass;

-- ── Enable + FORCE row security on every tenant table ──────────────────────────
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'projects','project_memberships','reporting_periods','estimate_versions',
        'norms','norm_materials','estimate_packages','boq_items','boq_norm_mappings',
        'estimate_resource_lines','cost_centres','cost_centre_baselines',
        'cost_centre_plan_periods','cost_centre_periods','period_cost_deltas'
    ] LOOP
        EXECUTE format('ALTER TABLE qs.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE qs.%I FORCE  ROW LEVEL SECURITY', t);
    END LOOP;
END
$$;

-- ── Policies ───────────────────────────────────────────────────────────────────
-- projects: visible iff the current user is a member; the selected project must match.
CREATE POLICY p_projects ON qs.projects
    USING (id = qs.fn_current_project_id() AND qs.fn_is_member(id))
    WITH CHECK (id = qs.fn_current_project_id() AND qs.fn_is_member(id));

-- project_memberships: NON-RECURSIVE. Keyed only on the current user id — references no other
-- table, so tenant policies that call fn_is_member (which reads this table) cannot recurse.
CREATE POLICY p_memberships ON qs.project_memberships
    USING (user_id = qs.fn_current_user_id())
    WITH CHECK (user_id = qs.fn_current_user_id());

-- Every other tenant table: row's project must equal the selected project AND the user must
-- be a member of it. Applied uniformly via a loop.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'reporting_periods','estimate_versions','norms','norm_materials','estimate_packages',
        'boq_items','boq_norm_mappings','estimate_resource_lines','cost_centres',
        'cost_centre_baselines','cost_centre_plan_periods','cost_centre_periods','period_cost_deltas'
    ] LOOP
        EXECUTE format($f$
            CREATE POLICY p_%1$s ON qs.%1$I
                USING (project_id = qs.fn_current_project_id() AND qs.fn_is_member(project_id))
                WITH CHECK (project_id = qs.fn_current_project_id() AND qs.fn_is_member(project_id))
        $f$, t);
    END LOOP;
END
$$;
