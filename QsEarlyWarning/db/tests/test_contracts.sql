-- test_contracts.sql — Phase-0 behavioral gate. Run after apply.sh + seed.sql.
-- Any failed ASSERT aborts under ON_ERROR_STOP and fails the suite.
--
-- Ids are resolved from natural keys (as superuser) and stashed in session GUCs (app.t_*), which
-- DO blocks read via current_setting(). This avoids relying on psql :var interpolation inside
-- dollar-quoted bodies (which psql does not perform). RLS context uses the real app.current_* GUCs.
\set ON_ERROR_STOP on
SET search_path = qs, public;

SELECT id AS a   FROM qs.projects           WHERE slug='tower-a' \gset
SELECT id AS b   FROM qs.projects           WHERE slug='tower-b' \gset
SELECT id AS av1 FROM qs.estimate_versions  WHERE project_id=:a AND version_no=1 \gset
SELECT id AS av2 FROM qs.estimate_versions  WHERE project_id=:a AND version_no=2 \gset
SELECT id AS arp1 FROM qs.reporting_periods WHERE project_id=:a AND period_id=1 \gset
SELECT id AS arp2 FROM qs.reporting_periods WHERE project_id=:a AND period_id=2 \gset
SELECT id AS cc1 FROM qs.cost_centres       WHERE project_id=:a AND bcc_id='BCC-A-1' \gset
SELECT id AS cc2 FROM qs.cost_centres       WHERE project_id=:a AND bcc_id='BCC-A-2' \gset

-- stash ids as session GUCs for DO blocks
SELECT set_config('app.t_a',   :'a',   false), set_config('app.t_b',    :'b',    false),
       set_config('app.t_av1', :'av1', false), set_config('app.t_av2',  :'av2',  false),
       set_config('app.t_arp1',:'arp1',false), set_config('app.t_arp2', :'arp2', false),
       set_config('app.t_cc1', :'cc1', false), set_config('app.t_cc2',  :'cc2',  false);

-- ══ T1: publish happy path; CC2 ends at 90% and still publishes (Finding 1) ══
\echo T1 publish av1 (plan curve ends < 100%, monotonic, covered, rollup ok)
BEGIN;
  SET LOCAL ROLE qs_app;
  SET LOCAL app.current_user_id = '3';
  SET LOCAL app.current_project_id = :'a';
  CALL qs.sp_publish_estimate_version(:a, :av1);
COMMIT;
DO $$ BEGIN
  ASSERT (SELECT status FROM qs.estimate_versions WHERE id = current_setting('app.t_av1')::bigint) = 'published', 'av1 should be published';
  ASSERT (SELECT active_estimate_version_id FROM qs.projects WHERE id = current_setting('app.t_a')::bigint) = current_setting('app.t_av1')::bigint, 'project A active version should be av1';
END $$;

-- ══ T2: decreasing plan curve blocks publish ══
\echo T2 publish av2 must fail on decreasing plan curve
BEGIN;
  SET LOCAL ROLE qs_app;
  SET LOCAL app.current_user_id = '3';
  SET LOCAL app.current_project_id = :'a';
  DO $$
  DECLARE ok boolean := false; msg text; v_av2 bigint := current_setting('app.t_av2')::bigint; v_a bigint := current_setting('app.t_a')::bigint;
  BEGIN
    BEGIN
      CALL qs.sp_publish_estimate_version(v_a, v_av2);
    EXCEPTION WHEN others THEN
      GET STACKED DIAGNOSTICS msg = MESSAGE_TEXT; ok := true;
      ASSERT msg LIKE '%publish_validation_failed%' AND msg LIKE '%decreasing_plan%',
             format('expected decreasing_plan failure, got: %s', msg);
    END;
    ASSERT ok, 'publishing av2 should have failed';
  END $$;
ROLLBACK;

-- ══ T3: period open snapshots one fact per active centre; generated PV correct ══
\echo T3 open periods 1 and 2
BEGIN;
  SET LOCAL ROLE qs_app;
  SET LOCAL app.current_user_id = '3';
  SET LOCAL app.current_project_id = :'a';
  CALL qs.sp_open_period(:a, :arp1);
  CALL qs.sp_open_period(:a, :arp2);
  DO $$
  DECLARE v_a bigint := current_setting('app.t_a')::bigint;
          v_rp1 bigint := current_setting('app.t_arp1')::bigint;
          v_cc1 bigint := current_setting('app.t_cc1')::bigint;
  BEGIN
    ASSERT (SELECT count(*) FROM qs.cost_centre_periods WHERE project_id=v_a AND reporting_period_id=v_rp1) = 2, 'p1 should have 2 facts';
    ASSERT (SELECT bac_amount  FROM qs.cost_centre_periods WHERE project_id=v_a AND reporting_period_id=v_rp1 AND cost_centre_id=v_cc1) = 100000.00, 'cc1 bac snapshot';
    ASSERT (SELECT planned_pct FROM qs.cost_centre_periods WHERE project_id=v_a AND reporting_period_id=v_rp1 AND cost_centre_id=v_cc1) = 30, 'cc1 planned_pct snapshot';
    ASSERT (SELECT pv_amount   FROM qs.cost_centre_periods WHERE project_id=v_a AND reporting_period_id=v_rp1 AND cost_centre_id=v_cc1) = 30000.00, 'cc1 generated pv';
  END $$;
COMMIT;

-- ══ T4: snapshot cols immutable to app (privilege); actuals writable on open period ══
\echo T4 app cannot write snapshot cols; can write actuals on open period
BEGIN;
  SET LOCAL ROLE qs_app;
  SET LOCAL app.current_user_id = '1';
  SET LOCAL app.current_project_id = :'a';
  DO $$
  DECLARE ok boolean := false;
          v_a bigint := current_setting('app.t_a')::bigint;
          v_rp1 bigint := current_setting('app.t_arp1')::bigint;
          v_cc1 bigint := current_setting('app.t_cc1')::bigint;
  BEGIN
    BEGIN
      UPDATE qs.cost_centre_periods SET bac_amount = 1 WHERE project_id=v_a AND reporting_period_id=v_rp1 AND cost_centre_id=v_cc1;
    EXCEPTION WHEN insufficient_privilege THEN ok := true;
    END;
    ASSERT ok, 'qs_app must NOT UPDATE bac_amount (snapshot column)';
  END $$;
  UPDATE qs.cost_centre_periods SET actual_pct_complete = 40 WHERE project_id=:a AND reporting_period_id=:arp1 AND cost_centre_id=:cc1;
  DO $$
  DECLARE v_a bigint := current_setting('app.t_a')::bigint;
          v_rp1 bigint := current_setting('app.t_arp1')::bigint;
          v_cc1 bigint := current_setting('app.t_cc1')::bigint;
  BEGIN
    ASSERT (SELECT ev_amount FROM qs.cost_centre_periods WHERE project_id=v_a AND reporting_period_id=v_rp1 AND cost_centre_id=v_cc1) = 40000.00, 'generated ev = 40% of 100000';
  END $$;
COMMIT;

-- Remove one active-centre fact so period-2 close is incomplete.
DELETE FROM qs.cost_centre_periods WHERE project_id=:a AND reporting_period_id=:arp2 AND cost_centre_id=:cc2;

-- ══ T5a: incomplete period close fails with typed list ══
\echo T5a close period 2 must fail listing BCC-A-2
BEGIN;
  SET LOCAL ROLE qs_app;
  SET LOCAL app.current_user_id = '1';
  SET LOCAL app.current_project_id = :'a';
  DO $$
  DECLARE ok boolean := false; msg text;
          v_a bigint := current_setting('app.t_a')::bigint;
          v_rp2 bigint := current_setting('app.t_arp2')::bigint;
  BEGIN
    BEGIN
      CALL qs.sp_close_period(v_a, v_rp2);
    EXCEPTION WHEN others THEN
      GET STACKED DIAGNOSTICS msg = MESSAGE_TEXT; ok := true;
      ASSERT msg LIKE '%period_close_incomplete%' AND msg LIKE '%BCC-A-2%', format('expected incomplete-close listing BCC-A-2, got: %s', msg);
    END;
    ASSERT ok, 'incomplete close should fail';
  END $$;
ROLLBACK;

-- ══ T5b: complete period closes ══
\echo T5b close period 1 (complete) succeeds
BEGIN;
  SET LOCAL ROLE qs_app;
  SET LOCAL app.current_user_id = '1';
  SET LOCAL app.current_project_id = :'a';
  CALL qs.sp_close_period(:a, :arp1);
COMMIT;
DO $$ BEGIN
  ASSERT (SELECT status FROM qs.reporting_periods WHERE id=current_setting('app.t_arp1')::bigint) = 'closed', 'period 1 should be closed';
END $$;

-- ══ T5c: closed-period fact frozen even for actual-column update ══
\echo T5c closed-period fact is immutable
BEGIN;
  SET LOCAL ROLE qs_app;
  SET LOCAL app.current_user_id = '1';
  SET LOCAL app.current_project_id = :'a';
  DO $$
  DECLARE ok boolean := false; msg text;
          v_a bigint := current_setting('app.t_a')::bigint;
          v_rp1 bigint := current_setting('app.t_arp1')::bigint;
          v_cc1 bigint := current_setting('app.t_cc1')::bigint;
  BEGIN
    BEGIN
      UPDATE qs.cost_centre_periods SET actual_pct_complete = 99 WHERE project_id=v_a AND reporting_period_id=v_rp1 AND cost_centre_id=v_cc1;
    EXCEPTION WHEN others THEN
      GET STACKED DIAGNOSTICS msg = MESSAGE_TEXT; ok := true;
      ASSERT msg LIKE '%closed period%', format('expected closed-period freeze, got: %s', msg);
    END;
    ASSERT ok, 'updating a closed-period fact should fail';
  END $$;
COMMIT;

-- ══ T5d: rebaseline of a closed period rejected ══
\echo T5d rebaseline of closed period rejected
BEGIN;
  SET LOCAL ROLE qs_app;
  SET LOCAL app.current_user_id = '1';
  SET LOCAL app.current_project_id = :'a';
  DO $$
  DECLARE ok boolean := false;
          v_a bigint := current_setting('app.t_a')::bigint;
          v_rp1 bigint := current_setting('app.t_arp1')::bigint;
  BEGIN
    BEGIN
      CALL qs.sp_rebaseline_period(v_a, v_rp1);
    EXCEPTION WHEN others THEN ok := true;
    END;
    ASSERT ok, 'rebaselining a closed period should fail';
  END $$;
ROLLBACK;

-- ══ T6: RLS tenant isolation on reads ══
\echo T6 tenant isolation (member/spoof/no-context/multi-project/worker)
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'a';
  DO $$ BEGIN ASSERT (SELECT count(*) FROM qs.cost_centres) = 2, 'user1@A sees 2 centres'; END $$;
COMMIT;
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'b';
  DO $$ BEGIN ASSERT (SELECT count(*) FROM qs.cost_centres) = 0, 'user1 spoofing B (not a member) sees 0'; END $$;
COMMIT;
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1';
  DO $$ BEGIN ASSERT (SELECT count(*) FROM qs.cost_centres) = 0, 'no project context sees 0'; END $$;
COMMIT;
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='3'; SET LOCAL app.current_project_id=:'b';
  DO $$ BEGIN ASSERT (SELECT count(*) FROM qs.cost_centres) = 1, 'user3@B (multi-project) sees B'; END $$;
COMMIT;
BEGIN;
  SET LOCAL ROLE qs_worker; SET LOCAL app.current_user_id='900'; SET LOCAL app.current_project_id=:'a';
  DO $$ BEGIN ASSERT (SELECT count(*) FROM qs.cost_centres) = 2, 'worker service principal @A sees A'; END $$;
COMMIT;

-- ══ T7: security_invoker EVM view enforces querying role's RLS ══
\echo T7 EVM view isolation
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'a';
  DO $$
  DECLARE v_a bigint := current_setting('app.t_a')::bigint;
  BEGIN
    ASSERT (SELECT count(*) FROM qs.cost_centre_evm) = 3, 'view shows exactly A''s 3 facts';
    ASSERT (SELECT bool_and(project_id = v_a) FROM qs.cost_centre_evm), 'view rows all belong to A';
  END $$;
COMMIT;
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'b';
  DO $$ BEGIN ASSERT (SELECT count(*) FROM qs.cost_centre_evm) = 0, 'view leaks nothing to a non-member'; END $$;
COMMIT;

-- ══ T8: non-recursive membership policy (per-user, no recursion) ══
\echo T8 membership visibility is per-user and non-recursive
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='3'; SET LOCAL app.current_project_id=:'a';
  DO $$ BEGIN
    ASSERT (SELECT count(*) FROM qs.project_memberships) = 2, 'user3 sees only their own 2 membership rows';
    ASSERT (SELECT bool_and(user_id = 3) FROM qs.project_memberships), 'no other users'' memberships visible';
  END $$;
COMMIT;

\echo ALL CONTRACT TESTS PASSED
