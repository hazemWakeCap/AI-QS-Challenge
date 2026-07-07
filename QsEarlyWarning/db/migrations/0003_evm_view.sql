-- 0003_evm_view.sql — the single computed-EVM source of truth (plan §5.1, §5.0 Choice 2)
--
-- SECURITY: security_invoker view (Finding 2 of 3rd review) — runs as the querying role so RLS on
-- the underlying tenant tables applies. Requires PostgreSQL 15+.
--
-- ACTUAL COST is resolved ONCE, canonically, in the `base` CTE as `ac_eff`:
--   * before the ledger cutover (projects.ledger_active = false): the on-fact cumulative
--     ac_total_amount (import-compatible interim, Choice 2);
--   * after cutover (ledger_active = true): the ledger-derived cumulative from period_cost_deltas,
--     which becomes the single writable source of AC. The fact ac_* columns are frozen at cutover.
-- Every EVM figure below is computed from ac_eff, so there is never a second AC source. For a
-- non-cutover project ac_eff == the fact total, so the numbers are byte-identical to before.
--
-- The workbook works in whole AED (EV/PV/AC are integers); ratios use whole-AED EV/PV/AC so indices
-- match the workbook exactly at small magnitudes.

SET search_path = qs, public;

CREATE VIEW qs.cost_centre_evm
WITH (security_invoker = true) AS
WITH base AS (
    SELECT
        f.project_id, f.id, f.cost_centre_id, f.reporting_period_id, f.estimate_version_id,
        f.lifecycle, f.bac_amount, f.budget_qty, f.planned_pct, f.actual_pct_complete,
        f.pv_amount, f.ev_amount, f.earned_qty,
        f.ac_material_amount, f.ac_manpower_amount, f.ac_equipment_amount, f.ac_subcontract_amount,
        cc.bcc_id, cc.package_code, cc.wbs_code, cc.discipline,
        rp.period_id, rp.period_start,
        -- canonical actual cost: ledger-derived when active, else the on-fact cumulative total
        CASE WHEN p.ledger_active THEN COALESCE(led.cum_ac, 0) ELSE f.ac_total_amount END AS ac_eff
    FROM qs.cost_centre_periods f
    JOIN qs.cost_centres      cc ON cc.project_id = f.project_id AND cc.id = f.cost_centre_id
    JOIN qs.reporting_periods rp ON rp.project_id = f.project_id AND rp.id = f.reporting_period_id
    JOIN qs.projects          p  ON p.id = f.project_id
    LEFT JOIN LATERAL (
        -- cumulative signed ledger balance for this centre up to this period's ordinal
        SELECT sum(CASE d.direction WHEN 'POSTING' THEN d.amount ELSE -d.amount END) AS cum_ac
        FROM qs.period_cost_deltas d
        JOIN qs.reporting_periods drp ON drp.project_id = d.project_id AND drp.id = d.reporting_period_id
        WHERE d.project_id = f.project_id AND d.cost_centre_id = f.cost_centre_id
          AND drp.period_id <= rp.period_id
    ) led ON true
)
SELECT
    project_id,
    id                       AS cost_centre_period_id,
    cost_centre_id,
    reporting_period_id,
    estimate_version_id,
    bcc_id, package_code, wbs_code, discipline,
    period_id, period_start, lifecycle,
    bac_amount, budget_qty, planned_pct, actual_pct_complete,
    pv_amount, ev_amount, earned_qty,
    round(pv_amount, 0) AS pv_whole,
    round(ev_amount, 0) AS ev_whole,
    ac_eff              AS ac_total_amount,
    ac_material_amount, ac_manpower_amount, ac_equipment_amount, ac_subcontract_amount,
    (round(ev_amount, 0) - round(ac_eff, 0))                          AS cv_amount,
    (round(ev_amount, 0) / NULLIF(round(ac_eff, 0), 0))               AS cpi,
    (round(ev_amount, 0) / NULLIF(round(pv_amount, 0), 0))            AS spi,
    CASE WHEN round(ev_amount, 0) IS NULL OR round(ev_amount, 0) = 0 THEN bac_amount
         ELSE round(bac_amount * round(ac_eff, 0) / round(ev_amount, 0), 2) END AS eac_amount,
    CASE WHEN round(ev_amount, 0) IS NULL OR round(ev_amount, 0) = 0 THEN 0
         ELSE bac_amount - round(bac_amount * round(ac_eff, 0) / round(ev_amount, 0), 2) END AS vac_amount,
    (100.0 * round(ac_eff, 0) / NULLIF(bac_amount, 0))               AS pct_budget_consumed,
    CASE
        WHEN lifecycle = 'NOT_STARTED' THEN 'NOT_STARTED'
        WHEN lifecycle = 'CLOSED'      THEN 'CLOSED'
        WHEN (round(ev_amount, 0) / NULLIF(round(ac_eff, 0), 0)) IS NULL THEN 'GREEN'
        WHEN (round(ev_amount, 0) / NULLIF(round(ac_eff, 0), 0)) < 0.95 THEN 'AMBER'
        ELSE 'GREEN'
    END                                                              AS alert_level
FROM base;
