-- seed.sql — deterministic two-project fixture, inserted as a superuser (bypasses RLS).
-- Shapes chosen to exercise every Phase-0 contract, including the "plan curve may end < 100%"
-- rule (CC2 ends at 90% and must still publish).
\set ON_ERROR_STOP on
SET search_path = qs, public;

-- ── Project A (tower-a) ─────────────────────────────────────────────────────
INSERT INTO qs.projects (slug, name, reporting_currency) VALUES ('tower-a','Tower A','AED')
    RETURNING id AS a \gset

INSERT INTO qs.reporting_periods (project_id, period_id, period_start) VALUES
    (:a, 1, DATE '2024-01-01'), (:a, 2, DATE '2024-02-01'), (:a, 3, DATE '2024-03-01');

INSERT INTO qs.estimate_versions (project_id, version_no, status, schedule_horizon_period_id)
    VALUES (:a, 1, 'draft', 3) RETURNING id AS av1 \gset

INSERT INTO qs.estimate_packages (project_id, estimate_version_id, code, name)
    VALUES (:a, :av1, 'EP-001', 'Structures') RETURNING id AS apkg \gset

-- CC1 finishes at period 3 (within horizon 3) → its curve MUST reach 100 at p3.
-- CC2 has no planned-finish → its curve may end below 100 (ends at 90).
INSERT INTO qs.cost_centres (project_id, bcc_id, package_code, estimate_package_id,
                             effective_start_period, effective_end_period, planned_finish_period_id, is_plan_complete)
    VALUES (:a, 'BCC-A-1', 'EP-001', :apkg, 1, 3, 3, false) RETURNING id AS cc1 \gset
INSERT INTO qs.cost_centres (project_id, bcc_id, package_code, estimate_package_id,
                             effective_start_period, effective_end_period, planned_finish_period_id, is_plan_complete)
    VALUES (:a, 'BCC-A-2', 'EP-001', :apkg, 1, 3, NULL, false) RETURNING id AS cc2 \gset

INSERT INTO qs.cost_centre_baselines (project_id, estimate_version_id, cost_centre_id, bac_amount, budget_qty)
    VALUES (:a, :av1, :cc1, 100000.00, 1000.0), (:a, :av1, :cc2, 50000.00, 500.0);

-- reporting_period ids for A
SELECT id AS arp1 FROM qs.reporting_periods WHERE project_id=:a AND period_id=1 \gset
SELECT id AS arp2 FROM qs.reporting_periods WHERE project_id=:a AND period_id=2 \gset
SELECT id AS arp3 FROM qs.reporting_periods WHERE project_id=:a AND period_id=3 \gset

-- monotonic curves; CC1 reaches 100 at p3, CC2 ends at 90 (< 100, allowed)
INSERT INTO qs.cost_centre_plan_periods (project_id, estimate_version_id, cost_centre_id, reporting_period_id, planned_pct) VALUES
    (:a, :av1, :cc1, :arp1, 30), (:a, :av1, :cc1, :arp2, 65), (:a, :av1, :cc1, :arp3, 100),
    (:a, :av1, :cc2, :arp1, 20), (:a, :av1, :cc2, :arp2, 55), (:a, :av1, :cc2, :arp3, 90);

-- one BOQ item whose resource lines roll up exactly to its total (rollup passes at publish)
INSERT INTO qs.boq_items (project_id, estimate_version_id, boq_sec, item_ref, total_amount)
    VALUES (:a, :av1, 'S1', 'I1', 100.00) RETURNING id AS aboq \gset
INSERT INTO qs.estimate_resource_lines (project_id, estimate_version_id, boq_item_id, rtype, quantity, unit_rate_amount) VALUES
    (:a, :av1, :aboq, 'MANPOWER', 6, 10.00),   -- 60.00
    (:a, :av1, :aboq, 'MATERIAL', 4, 10.00);   -- 40.00  → sum 100.00 == total

-- A second, DELIBERATELY BROKEN draft version for the decreasing-curve publish test.
INSERT INTO qs.estimate_versions (project_id, version_no, status, schedule_horizon_period_id)
    VALUES (:a, 2, 'draft', 3) RETURNING id AS av2 \gset
INSERT INTO qs.cost_centre_baselines (project_id, estimate_version_id, cost_centre_id, bac_amount, budget_qty)
    VALUES (:a, :av2, :cc1, 100000.00, 1000.0), (:a, :av2, :cc2, 50000.00, 500.0);
INSERT INTO qs.cost_centre_plan_periods (project_id, estimate_version_id, cost_centre_id, reporting_period_id, planned_pct) VALUES
    (:a, :av2, :cc1, :arp1, 30), (:a, :av2, :cc1, :arp2, 65), (:a, :av2, :cc1, :arp3, 50),  -- 65 -> 50 DECREASING
    (:a, :av2, :cc2, :arp1, 20), (:a, :av2, :cc2, :arp2, 55), (:a, :av2, :cc2, :arp3, 90);

-- ── Project B (tower-b) — a separate tenant for isolation tests ─────────────
INSERT INTO qs.projects (slug, name, reporting_currency) VALUES ('tower-b','Tower B','AED')
    RETURNING id AS b \gset
INSERT INTO qs.reporting_periods (project_id, period_id, period_start) VALUES (:b, 1, DATE '2024-01-01');
INSERT INTO qs.estimate_versions (project_id, version_no, status, schedule_horizon_period_id)
    VALUES (:b, 1, 'draft', 1) RETURNING id AS bv1 \gset
INSERT INTO qs.cost_centres (project_id, bcc_id, effective_start_period, effective_end_period)
    VALUES (:b, 'BCC-B-1', 1, 1) RETURNING id AS bcc1 \gset
INSERT INTO qs.cost_centre_baselines (project_id, estimate_version_id, cost_centre_id, bac_amount, budget_qty)
    VALUES (:b, :bv1, :bcc1, 9999.00, 99.0);

-- ── Memberships (identity behind RLS) ───────────────────────────────────────
--  user 1 → A only ;  user 2 → B only ;  user 3 → both ;  user 900 → A (service principal)
INSERT INTO qs.project_memberships (project_id, user_id, role) VALUES
    (:a, 1, 'editor'), (:b, 2, 'editor'),
    (:a, 3, 'editor'), (:b, 3, 'editor'),
    (:a, 900, 'service');

\echo seed complete: A=:a B=:b av1=:av1 av2=:av2 cc1=:cc1 cc2=:cc2
