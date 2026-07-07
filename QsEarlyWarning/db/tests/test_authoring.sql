-- test_authoring.sql — Phase-4 estimate authoring/publication lifecycle (self-contained).
-- Run after apply.sh. Any failed ASSERT aborts under ON_ERROR_STOP.
\set ON_ERROR_STOP on
SET search_path = qs, public;

-- ── seed: one project, two DRAFT versions, one cost centre covered by both (as superuser) ──
INSERT INTO qs.projects (slug, name, reporting_currency) VALUES ('authoring-proj','Authoring','AED') RETURNING id AS ap \gset
INSERT INTO qs.project_memberships (project_id, user_id, role) VALUES (:ap, 1, 'owner');
INSERT INTO qs.reporting_periods (project_id, period_id, period_start) VALUES (:ap,1,DATE '2024-01-01'), (:ap,2,DATE '2024-02-01');
INSERT INTO qs.estimate_versions (project_id, version_no, status, schedule_horizon_period_id) VALUES (:ap,1,'draft',2) RETURNING id AS av1 \gset
INSERT INTO qs.estimate_versions (project_id, version_no, status, schedule_horizon_period_id) VALUES (:ap,2,'draft',2) RETURNING id AS av2 \gset
INSERT INTO qs.cost_centres (project_id, bcc_id, effective_start_period, effective_end_period) VALUES (:ap,'CC-A',1,2) RETURNING id AS acc \gset
INSERT INTO qs.cost_centre_baselines (project_id, estimate_version_id, cost_centre_id, bac_amount, budget_qty) VALUES
    (:ap,:av1,:acc,1000,100), (:ap,:av2,:acc,1200,100);
SELECT id AS arp1 FROM qs.reporting_periods WHERE project_id=:ap AND period_id=1 \gset
SELECT id AS arp2 FROM qs.reporting_periods WHERE project_id=:ap AND period_id=2 \gset
-- monotonic plan curves, both end < 100% (CC-A has no planned_finish → allowed)
INSERT INTO qs.cost_centre_plan_periods (project_id, estimate_version_id, cost_centre_id, reporting_period_id, planned_pct) VALUES
    (:ap,:av1,:acc,:arp1,50), (:ap,:av1,:acc,:arp2,90),
    (:ap,:av2,:acc,:arp1,40), (:ap,:av2,:acc,:arp2,80);
SELECT set_config('app.t_ap',:'ap',false), set_config('app.t_av1',:'av1',false),
       set_config('app.t_av2',:'av2',false), set_config('app.t_acc',:'acc',false);

-- ══ A1: publish v1 → active version = v1 ══
\echo A1 publish v1
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'ap';
  CALL qs.sp_publish_estimate_version(:ap, :av1);
COMMIT;
DO $$ BEGIN
  ASSERT (SELECT status FROM qs.estimate_versions WHERE id=current_setting('app.t_av1')::bigint) = 'published', 'v1 published';
  ASSERT (SELECT active_estimate_version_id FROM qs.projects WHERE id=current_setting('app.t_ap')::bigint) = current_setting('app.t_av1')::bigint, 'active = v1';
END $$;

-- ══ A2: editing the PUBLISHED version's graph is rejected ══
\echo A2 edit published v1 graph rejected
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'ap';
  DO $$
  DECLARE ok boolean := false; msg text;
          ap bigint := current_setting('app.t_ap')::bigint; av1 bigint := current_setting('app.t_av1')::bigint;
  BEGIN
    BEGIN
      UPDATE qs.cost_centre_baselines SET bac_amount = 999 WHERE project_id=ap AND estimate_version_id=av1;
    EXCEPTION WHEN others THEN GET STACKED DIAGNOSTICS msg=MESSAGE_TEXT; ok:=true; ASSERT msg LIKE '%immutable%', msg;
    END;
    ASSERT ok, 'editing a published version must be rejected';
  END $$;
COMMIT;

-- ══ A3: editing the still-DRAFT version is allowed ══
\echo A3 edit draft v2 graph allowed
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'ap';
  UPDATE qs.cost_centre_baselines SET bac_amount = 1300 WHERE project_id=:ap AND estimate_version_id=:av2;
  DO $$
  DECLARE ap bigint := current_setting('app.t_ap')::bigint; av2 bigint := current_setting('app.t_av2')::bigint;
  BEGIN
    ASSERT (SELECT bac_amount FROM qs.cost_centre_baselines WHERE project_id=ap AND estimate_version_id=av2) = 1300, 'draft edit applied';
  END $$;
COMMIT;

-- ══ A4: publishing v2 supersedes v1 and repoints the active version ══
\echo A4 publish v2 supersedes v1
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'ap';
  CALL qs.sp_publish_estimate_version(:ap, :av2);
COMMIT;
DO $$
DECLARE ap bigint := current_setting('app.t_ap')::bigint;
        av1 bigint := current_setting('app.t_av1')::bigint; av2 bigint := current_setting('app.t_av2')::bigint;
BEGIN
  ASSERT (SELECT status FROM qs.estimate_versions WHERE id=av1) = 'superseded', 'v1 superseded';
  ASSERT (SELECT status FROM qs.estimate_versions WHERE id=av2) = 'published', 'v2 published';
  ASSERT (SELECT active_estimate_version_id FROM qs.projects WHERE id=ap) = av2, 'active repointed to v2';
END $$;

-- ══ A5: editing the now-SUPERSEDED v1 is still rejected ══
\echo A5 edit superseded v1 rejected
BEGIN;
  SET LOCAL ROLE qs_app; SET LOCAL app.current_user_id='1'; SET LOCAL app.current_project_id=:'ap';
  DO $$
  DECLARE ok boolean := false;
          ap bigint := current_setting('app.t_ap')::bigint; av1 bigint := current_setting('app.t_av1')::bigint;
  BEGIN
    BEGIN
      UPDATE qs.cost_centre_plan_periods SET planned_pct = 10 WHERE project_id=ap AND estimate_version_id=av1;
    EXCEPTION WHEN others THEN ok:=true;
    END;
    ASSERT ok, 'editing a superseded version must be rejected';
  END $$;
COMMIT;

\echo ALL AUTHORING TESTS PASSED
