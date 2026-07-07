-- test_ledger.sql — Phase-3 cost-ledger contracts (self-contained; seeds its own project).
-- Run after apply.sh. Any failed ASSERT aborts under ON_ERROR_STOP.
\set ON_ERROR_STOP on
SET search_path = qs, public;

-- ── seed a tiny project with cumulative-snapshot facts (as superuser, bypasses RLS) ──
INSERT INTO qs.projects (slug, name, reporting_currency) VALUES ('ledger-proj','Ledger','AED') RETURNING id AS lp \gset
INSERT INTO qs.project_memberships (project_id, user_id, role) VALUES (:lp, 1, 'owner');
INSERT INTO qs.reporting_periods (project_id, period_id, period_start) VALUES
    (:lp,1,DATE '2024-01-01'), (:lp,2,DATE '2024-02-01'), (:lp,3,DATE '2024-03-01');
INSERT INTO qs.estimate_versions (project_id, version_no, status, schedule_horizon_period_id)
    VALUES (:lp, 1, 'published', 3) RETURNING id AS lev \gset
UPDATE qs.projects SET active_estimate_version_id = :lev WHERE id = :lp;
INSERT INTO qs.cost_centres (project_id, bcc_id, effective_start_period, effective_end_period)
    VALUES (:lp, 'CC-L1', 1, 3) RETURNING id AS lcc \gset
INSERT INTO qs.cost_centre_baselines (project_id, estimate_version_id, cost_centre_id, bac_amount, budget_qty)
    VALUES (:lp, :lev, :lcc, 1000, 100) RETURNING id AS lbl \gset
SELECT id AS lrp1 FROM qs.reporting_periods WHERE project_id=:lp AND period_id=1 \gset
SELECT id AS lrp3 FROM qs.reporting_periods WHERE project_id=:lp AND period_id=3 \gset

-- cumulative AC splits: P1 100 (60+40), P2 250 (150+100), P3 400 (240+160); ev = actual%×bac
INSERT INTO qs.cost_centre_periods
    (project_id, cost_centre_id, reporting_period_id, baseline_id, estimate_version_id,
     bac_amount, budget_qty, planned_pct, actual_pct_complete, ac_manpower_amount, ac_material_amount, lifecycle)
SELECT :lp, :lcc, rp.id, :lbl, :lev, 1000, 100, v.pct, v.pct, v.mp, v.mat, 'IN_PROGRESS'
FROM (VALUES (1,10,60,40),(2,25,150,100),(3,40,240,160)) AS v(pord,pct,mp,mat)
JOIN qs.reporting_periods rp ON rp.project_id=:lp AND rp.period_id=v.pord;

SELECT set_config('app.t_lp',:'lp',false), set_config('app.t_lcc',:'lcc',false),
       set_config('app.t_lrp1',:'lrp1',false), set_config('app.t_lrp3',:'lrp3',false);

-- ══ L1: cutover converts cumulative snapshots to per-resource deltas ══
\echo L1 cutover to ledger
BEGIN;
  SET LOCAL ROLE qs_worker; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'lp';
  CALL qs.sp_cutover_to_ledger(:lp);
COMMIT;
DO $$
DECLARE lp bigint := current_setting('app.t_lp')::bigint;
BEGIN
  ASSERT (SELECT ledger_active FROM qs.projects WHERE id=lp), 'project should be ledger_active';
  ASSERT (SELECT count(*) FROM qs.period_cost_deltas WHERE project_id=lp) = 6, 'expect 6 deltas (2 rtypes × 3 periods)';
END $$;

-- ══ L2: EVM reads ledger-derived cumulative AC and reconciles to the pre-cutover totals ══
\echo L2 EVM ledger-derived AC reconciles (100/250/400) and CPI holds
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'lp';
  DO $$
  DECLARE lp bigint := current_setting('app.t_lp')::bigint;
          rp1 bigint := current_setting('app.t_lrp1')::bigint;
          rp3 bigint := current_setting('app.t_lrp3')::bigint;
  BEGIN
    ASSERT (SELECT ac_total_amount FROM qs.cost_centre_evm WHERE project_id=lp AND reporting_period_id=rp1) = 100, 'P1 ledger cum = 100';
    ASSERT (SELECT ac_total_amount FROM qs.cost_centre_evm WHERE project_id=lp AND reporting_period_id=rp3) = 400, 'P3 ledger cum = 400';
    ASSERT (SELECT cpi FROM qs.cost_centre_evm WHERE project_id=lp AND reporting_period_id=rp3) = 1, 'P3 cpi = 1';
  END $$;
COMMIT;

-- ══ L3: capture is idempotent by key ══
\echo L3 idempotent posting
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'lp';
  CALL qs.sp_post_cost_delta(:lp, :lcc, :lrp3, 'MANPOWER', 25, 'POSTING', 'k1');
  CALL qs.sp_post_cost_delta(:lp, :lcc, :lrp3, 'MANPOWER', 25, 'POSTING', 'k1');   -- duplicate, no-op
COMMIT;
DO $$
DECLARE lp bigint := current_setting('app.t_lp')::bigint;
        rp3 bigint := current_setting('app.t_lrp3')::bigint;
BEGIN
  ASSERT (SELECT count(*) FROM qs.period_cost_deltas WHERE project_id=lp AND idempotency_key='k1') = 1, 'duplicate key inserted once';
  ASSERT (SELECT ac_total_amount FROM qs.cost_centre_evm WHERE project_id=lp AND reporting_period_id=rp3) = 425, 'P3 cum = 400 + 25 (once)';
END $$;

-- ══ L4: the ledger is append-only for the app role ══
\echo L4 append-only (no UPDATE/DELETE for qs_app)
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'lp';
  DO $$
  DECLARE ok boolean := false; lp bigint := current_setting('app.t_lp')::bigint;
  BEGIN
    BEGIN UPDATE qs.period_cost_deltas SET amount=1 WHERE project_id=lp; EXCEPTION WHEN insufficient_privilege THEN ok:=true; END;
    ASSERT ok, 'qs_app must NOT UPDATE ledger rows';
    ok := false;
    BEGIN DELETE FROM qs.period_cost_deltas WHERE project_id=lp; EXCEPTION WHEN insufficient_privilege THEN ok:=true; END;
    ASSERT ok, 'qs_app must NOT DELETE ledger rows';
  END $$;
COMMIT;

-- ══ L5: fact ac_* columns are frozen after cutover ══
\echo L5 fact ac_* frozen after cutover
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'lp';
  DO $$
  DECLARE ok boolean := false; msg text;
          lp bigint := current_setting('app.t_lp')::bigint;
          rp3 bigint := current_setting('app.t_lrp3')::bigint;
          lcc bigint := current_setting('app.t_lcc')::bigint;
  BEGIN
    BEGIN
      UPDATE qs.cost_centre_periods SET ac_material_amount=1 WHERE project_id=lp AND reporting_period_id=rp3 AND cost_centre_id=lcc;
    EXCEPTION WHEN others THEN GET STACKED DIAGNOSTICS msg=MESSAGE_TEXT; ok:=true; ASSERT msg LIKE '%frozen after the ledger cutover%', msg;
    END;
    ASSERT ok, 'fact ac_* update should be rejected after cutover';
  END $$;
COMMIT;

-- ══ L6: posting to a closed period is rejected ══
\echo L6 post to closed period rejected
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'lp';
  CALL qs.sp_close_period(:lp, :lrp1);
COMMIT;
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'lp';
  DO $$
  DECLARE ok boolean := false; msg text;
          lp bigint := current_setting('app.t_lp')::bigint;
          lcc bigint := current_setting('app.t_lcc')::bigint;
          rp1 bigint := current_setting('app.t_lrp1')::bigint;
  BEGIN
    BEGIN
      CALL qs.sp_post_cost_delta(lp, lcc, rp1, 'MANPOWER', 10, 'POSTING', 'k2');
    EXCEPTION WHEN others THEN GET STACKED DIAGNOSTICS msg=MESSAGE_TEXT; ok:=true; ASSERT msg LIKE '%closed period%', msg;
    END;
    ASSERT ok, 'posting to a closed period should fail';
  END $$;
COMMIT;

\echo ALL LEDGER TESTS PASSED
