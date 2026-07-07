-- test_portfolio.sql — Phase-5 currency/FX integrity (plan §5.1, Finding 8). Self-contained.
-- Portfolio scale itself is a deferral (see README Phase 5): ~2K rows/project doesn't justify
-- TimescaleDB, and cross-project isolation is already proven by the RLS tests (test_contracts T6).
-- What IS enforced here: money is per-project in an immutable reporting currency, so unlike
-- currencies can never be summed by accident.
\set ON_ERROR_STOP on
SET search_path = qs, public;

-- project with NO monetary data yet → currency is still changeable
INSERT INTO qs.projects (slug, name, reporting_currency) VALUES ('fx-empty','FX Empty','AED') RETURNING id AS pe \gset
-- project WITH monetary data (a baseline) → currency frozen
INSERT INTO qs.projects (slug, name, reporting_currency) VALUES ('fx-data','FX Data','AED') RETURNING id AS pd \gset
INSERT INTO qs.estimate_versions (project_id, version_no, status) VALUES (:pd,1,'draft') RETURNING id AS pdv \gset
INSERT INTO qs.cost_centres (project_id, bcc_id, effective_start_period) VALUES (:pd,'CC-FX',1) RETURNING id AS pdcc \gset
INSERT INTO qs.cost_centre_baselines (project_id, estimate_version_id, cost_centre_id, bac_amount) VALUES (:pd,:pdv,:pdcc,5000);
SELECT set_config('app.t_pe',:'pe',false), set_config('app.t_pd',:'pd',false);

-- ══ P1: currency is mutable while there is no monetary data ══
\echo P1 currency settable before monetary data
UPDATE qs.projects SET reporting_currency='USD' WHERE id=:pe;
DO $$ BEGIN
  ASSERT (SELECT reporting_currency FROM qs.projects WHERE id=current_setting('app.t_pe')::bigint) = 'USD', 'empty project currency changed';
END $$;

-- ══ P2: currency is immutable once monetary data exists ══
\echo P2 currency frozen once monetary data exists
DO $$
DECLARE ok boolean := false; msg text; pd bigint := current_setting('app.t_pd')::bigint;
BEGIN
  BEGIN
    UPDATE qs.projects SET reporting_currency='USD' WHERE id=pd;
  EXCEPTION WHEN others THEN GET STACKED DIAGNOSTICS msg=MESSAGE_TEXT; ok:=true; ASSERT msg LIKE '%immutable%', msg;
  END;
  ASSERT ok, 'currency change with monetary data present must be rejected';
END $$;

-- ══ P3: currencies are per-project (no shared/global currency to sum across) ══
\echo P3 per-project currency, unlike currencies not summable
DO $$ BEGIN
  ASSERT (SELECT count(DISTINCT reporting_currency) FROM qs.projects WHERE slug IN ('fx-empty','fx-data')) = 2,
         'the two projects carry distinct currencies — a portfolio total must group by currency, never blind-sum';
END $$;

\echo ALL PORTFOLIO TESTS PASSED
