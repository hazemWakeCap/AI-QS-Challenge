-- 0002_schema.sql — the tenant schema (plan §5, §5.0, §5.1)
--
-- Design invariants encoded here:
--  * Every table carries project_id and exposes UNIQUE(project_id, id) so children can use a
--    COMPOSITE FK (project_id, parent_id) -> parent(project_id, id): a child can never reference
--    a parent in a different project (plan Finding 5). Every FK is ON DELETE RESTRICT.
--  * Money is NUMERIC with neutral *_amount names; the reporting currency lives on projects and
--    is immutable once monetary data exists (trigger in 0005). No _aed in names (Finding 8).
--  * The authored estimate graph is owned by estimate_version_id with version-scoped uniqueness
--    (Finding 3); published versions become immutable.
--  * EVM inputs are snapshotted onto the fact so generated columns are row-local and valid
--    (Finding 1r / Choice 1). Cross-row rules (rollup, monotonic plan curve, completeness) are
--    NOT CHECKs — they are validated transactionally at publish / period-close (0005).
--
-- All objects are created in schema qs and owned by qs_owner (the apply script SET ROLEs to it).

CREATE SCHEMA IF NOT EXISTS qs;
SET search_path = qs, public;

-- ── Tenant root ───────────────────────────────────────────────────────────────
CREATE TABLE qs.projects (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    slug               text        NOT NULL UNIQUE,
    name               text        NOT NULL,
    reporting_currency text        NOT NULL CHECK (reporting_currency ~ '^[A-Z]{3}$'),
    data_revision      bigint      NOT NULL DEFAULT 0,
    status             text        NOT NULL DEFAULT 'active' CHECK (status IN ('active','archived')),
    -- Once true (after the one-time cutover, 0007), actual cost lives in the append-only ledger and
    -- the fact ac_* cumulative columns are frozen; the EVM view reads the ledger-derived total.
    ledger_active      boolean     NOT NULL DEFAULT false,
    -- active-version pointer (composite FK added by ALTER once estimate_versions exists)
    active_estimate_version_id bigint,
    created_at         timestamptz NOT NULL DEFAULT now()
);

-- ── Identity / authorization (plan §5.0 Choice 4) ──────────────────────────────
-- Membership rows drive RLS. A background service principal (qs_worker) gets its own rows here
-- too, so it is RLS-governed like any user rather than an anonymous bypass.
CREATE TABLE qs.project_memberships (
    id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id bigint NOT NULL REFERENCES qs.projects(id) ON DELETE RESTRICT,
    user_id    bigint NOT NULL,
    role       text   NOT NULL CHECK (role IN ('owner','editor','viewer','service')),
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_membership UNIQUE (project_id, user_id)
);

-- ── Reporting calendar (plan §5.1; Finding 4) ──────────────────────────────────
CREATE TABLE qs.reporting_periods (
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id   bigint NOT NULL REFERENCES qs.projects(id) ON DELETE RESTRICT,
    period_id    int    NOT NULL,                 -- ordinal 1..N
    period_start date   NOT NULL,
    status       text   NOT NULL DEFAULT 'open' CHECK (status IN ('open','closed')),
    opened_at    timestamptz,
    closed_at    timestamptz,
    CONSTRAINT uq_rp_pid   UNIQUE (project_id, id),
    CONSTRAINT uq_rp_ord   UNIQUE (project_id, period_id),
    CONSTRAINT uq_rp_start UNIQUE (project_id, period_start)
);

-- ── Versioned estimate graph (plan §5.0 Choice 3; Finding 3) ───────────────────
CREATE TABLE qs.estimate_versions (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id       bigint NOT NULL REFERENCES qs.projects(id) ON DELETE RESTRICT,
    version_no       int    NOT NULL,
    status           text   NOT NULL DEFAULT 'draft' CHECK (status IN ('draft','published','superseded')),
    effective_start  date,
    effective_end    date,
    source_hash      text,
    -- Declared schedule horizon: the last period this version's plan is expected to cover.
    -- Used by publish validation: a plan curve need only reach 100% when the centre's
    -- planned-finish period is within this horizon (Finding 1 of 3rd review).
    schedule_horizon_period_id int,
    published_at     timestamptz,
    created_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_ev_pid UNIQUE (project_id, id),
    CONSTRAINT uq_ev_no  UNIQUE (project_id, version_no)
);

-- projects.active_estimate_version_id -> estimate_versions, same project (composite FK).
ALTER TABLE qs.projects
    ADD CONSTRAINT fk_projects_active_version
    FOREIGN KEY (id, active_estimate_version_id)
    REFERENCES qs.estimate_versions (project_id, id) ON DELETE RESTRICT;

CREATE TABLE qs.norms (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id         bigint NOT NULL,
    estimate_version_id bigint NOT NULL,
    norm_code          text   NOT NULL,
    description        text,
    unit               text,
    output_norm        numeric(18,6),
    CONSTRAINT uq_norms_pid UNIQUE (project_id, id),
    CONSTRAINT uq_norms_code UNIQUE (estimate_version_id, norm_code),
    CONSTRAINT fk_norms_version FOREIGN KEY (project_id, estimate_version_id)
        REFERENCES qs.estimate_versions (project_id, id) ON DELETE RESTRICT
);

CREATE TABLE qs.norm_materials (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id    bigint NOT NULL,
    norm_id       bigint NOT NULL,
    material_code text   NOT NULL,
    qty_per_unit  numeric(18,6),
    CONSTRAINT uq_normmat_pid UNIQUE (project_id, id),
    CONSTRAINT fk_normmat_norm FOREIGN KEY (project_id, norm_id)
        REFERENCES qs.norms (project_id, id) ON DELETE RESTRICT
);

CREATE TABLE qs.estimate_packages (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id         bigint NOT NULL,
    estimate_version_id bigint NOT NULL,
    code               text   NOT NULL,           -- EP-…
    name               text,
    CONSTRAINT uq_pkg_pid  UNIQUE (project_id, id),
    CONSTRAINT uq_pkg_code UNIQUE (estimate_version_id, code),
    CONSTRAINT fk_pkg_version FOREIGN KEY (project_id, estimate_version_id)
        REFERENCES qs.estimate_versions (project_id, id) ON DELETE RESTRICT
);

CREATE TABLE qs.boq_items (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id         bigint NOT NULL,
    estimate_version_id bigint NOT NULL,
    boq_sec            text   NOT NULL,
    item_ref           text   NOT NULL,
    description        text,
    unit               text,
    quantity           numeric(18,4),
    norm_id            bigint,                     -- Norm Ref (nullable)
    total_amount       numeric(18,2),              -- TOTAL Amount; resource lines roll up to this
    CONSTRAINT uq_boq_pid  UNIQUE (project_id, id),
    CONSTRAINT uq_boq_item UNIQUE (estimate_version_id, boq_sec, item_ref),
    CONSTRAINT ck_boq_total_nonneg CHECK (total_amount IS NULL OR total_amount >= 0),
    CONSTRAINT fk_boq_version FOREIGN KEY (project_id, estimate_version_id)
        REFERENCES qs.estimate_versions (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_boq_norm FOREIGN KEY (project_id, norm_id)
        REFERENCES qs.norms (project_id, id) ON DELETE RESTRICT
);

CREATE TABLE qs.boq_norm_mappings (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id         bigint NOT NULL,
    estimate_version_id bigint NOT NULL,
    boq_item_id        bigint NOT NULL,
    norm_id            bigint NOT NULL,
    estimate_package_id bigint NOT NULL,
    CONSTRAINT uq_map_pid UNIQUE (project_id, id),
    CONSTRAINT fk_map_version FOREIGN KEY (project_id, estimate_version_id)
        REFERENCES qs.estimate_versions (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_map_boq FOREIGN KEY (project_id, boq_item_id)
        REFERENCES qs.boq_items (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_map_norm FOREIGN KEY (project_id, norm_id)
        REFERENCES qs.norms (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_map_pkg FOREIGN KEY (project_id, estimate_package_id)
        REFERENCES qs.estimate_packages (project_id, id) ON DELETE RESTRICT
);

CREATE TABLE qs.estimate_resource_lines (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id         bigint NOT NULL,
    estimate_version_id bigint NOT NULL,
    boq_item_id        bigint NOT NULL,
    norm_id            bigint,
    rtype              text   NOT NULL CHECK (rtype IN ('MANPOWER','MATERIAL','EQUIPMENT','SUBCONTRACT')),
    quantity           numeric(18,6),
    unit_rate_amount   numeric(18,4),
    -- row-local generated cost; the cross-row rollup to boq_items.total_amount is validated at publish.
    resource_cost_amount numeric(18,2)
        GENERATED ALWAYS AS (round(coalesce(quantity,0) * coalesce(unit_rate_amount,0), 2)) STORED,
    CONSTRAINT uq_rl_pid UNIQUE (project_id, id),
    CONSTRAINT fk_rl_version FOREIGN KEY (project_id, estimate_version_id)
        REFERENCES qs.estimate_versions (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_rl_boq FOREIGN KEY (project_id, boq_item_id)
        REFERENCES qs.boq_items (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_rl_norm FOREIGN KEY (project_id, norm_id)
        REFERENCES qs.norms (project_id, id) ON DELETE RESTRICT
);

-- ── Cost-centre master + effective range (plan §5.1; Finding 4r) ───────────────
CREATE TABLE qs.cost_centres (
    id                    bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id            bigint NOT NULL REFERENCES qs.projects(id) ON DELETE RESTRICT,
    bcc_id                text   NOT NULL,
    wbs_code              text,
    package_code          text,
    discipline            text,
    unit                  text,
    estimate_package_id   bigint,
    -- Active range: the calendar spine expects exactly one fact per active centre-period.
    effective_start_period int  NOT NULL,
    effective_end_period   int,
    -- Plan-completion signals for the "100% only if horizon reaches planned-finish" rule (Finding 1).
    planned_finish_period_id int,
    is_plan_complete       boolean NOT NULL DEFAULT false,
    CONSTRAINT uq_cc_pid UNIQUE (project_id, id),
    CONSTRAINT uq_cc_bcc UNIQUE (project_id, bcc_id),
    CONSTRAINT ck_cc_range CHECK (effective_end_period IS NULL OR effective_end_period >= effective_start_period),
    CONSTRAINT fk_cc_pkg FOREIGN KEY (project_id, estimate_package_id)
        REFERENCES qs.estimate_packages (project_id, id) ON DELETE RESTRICT
);

-- ── Baseline (stable numerics only) + time-phased plan curve (Choice 1; Finding 1r) ──
CREATE TABLE qs.cost_centre_baselines (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id         bigint NOT NULL,
    estimate_version_id bigint NOT NULL,
    cost_centre_id     bigint NOT NULL,
    bac_amount         numeric(18,2) NOT NULL CHECK (bac_amount >= 0),
    budget_qty         numeric(18,4),
    CONSTRAINT uq_bl_pid UNIQUE (project_id, id),
    CONSTRAINT uq_bl_cc  UNIQUE (project_id, estimate_version_id, cost_centre_id),
    CONSTRAINT fk_bl_version FOREIGN KEY (project_id, estimate_version_id)
        REFERENCES qs.estimate_versions (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_bl_cc FOREIGN KEY (project_id, cost_centre_id)
        REFERENCES qs.cost_centres (project_id, id) ON DELETE RESTRICT
);

CREATE TABLE qs.cost_centre_plan_periods (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id         bigint NOT NULL,
    estimate_version_id bigint NOT NULL,
    cost_centre_id     bigint NOT NULL,
    reporting_period_id bigint NOT NULL,
    -- ONE stored source of truth. planned_qty is derived (view) from budget_qty, not stored,
    -- so the two can never disagree. Range-only CHECK; monotonicity is a cross-row publish rule.
    planned_pct        numeric(7,4) NOT NULL CHECK (planned_pct >= 0 AND planned_pct <= 100),
    CONSTRAINT uq_plan_pid UNIQUE (project_id, id),
    CONSTRAINT uq_plan_key UNIQUE (project_id, estimate_version_id, cost_centre_id, reporting_period_id),
    CONSTRAINT fk_plan_version FOREIGN KEY (project_id, estimate_version_id)
        REFERENCES qs.estimate_versions (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_plan_cc FOREIGN KEY (project_id, cost_centre_id)
        REFERENCES qs.cost_centres (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_plan_rp FOREIGN KEY (project_id, reporting_period_id)
        REFERENCES qs.reporting_periods (project_id, id) ON DELETE RESTRICT
);

-- ── The fact: inputs vs snapshot vs row-local generated (plan §5.1) ────────────
CREATE TABLE qs.cost_centre_periods (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id         bigint NOT NULL,
    cost_centre_id     bigint NOT NULL,
    reporting_period_id bigint NOT NULL,
    baseline_id        bigint NOT NULL,
    estimate_version_id bigint NOT NULL,

    -- Snapshotted at period open; immutable. Column-level UPDATE is revoked from app/worker
    -- (0004) and a trigger freezes closed periods (0005). Written only by the period-open /
    -- rebaseline SECURITY DEFINER procedures.
    bac_amount         numeric(18,2),
    budget_qty         numeric(18,4),
    planned_pct        numeric(7,4) CHECK (planned_pct IS NULL OR (planned_pct >= 0 AND planned_pct <= 100)),

    -- Actual inputs (cumulative-to-date interim; ledger-derived post-cutover — Choice 2).
    actual_pct_complete numeric(7,4) CHECK (actual_pct_complete IS NULL OR (actual_pct_complete >= 0 AND actual_pct_complete <= 100)),
    ac_material_amount    numeric(18,2),
    ac_manpower_amount    numeric(18,2),
    ac_equipment_amount   numeric(18,2),
    ac_subcontract_amount numeric(18,2),
    lifecycle          text NOT NULL DEFAULT 'IN_PROGRESS'
                            CHECK (lifecycle IN ('NOT_STARTED','IN_PROGRESS','CLOSED')),

    -- Row-local generated (valid because every input is on-row).
    ac_total_amount    numeric(18,2)
        GENERATED ALWAYS AS (coalesce(ac_material_amount,0) + coalesce(ac_manpower_amount,0)
                           + coalesce(ac_equipment_amount,0) + coalesce(ac_subcontract_amount,0)) STORED,
    pv_amount          numeric(18,2)
        GENERATED ALWAYS AS (round(planned_pct / 100.0 * bac_amount, 2)) STORED,
    ev_amount          numeric(18,2)
        GENERATED ALWAYS AS (round(actual_pct_complete / 100.0 * bac_amount, 2)) STORED,
    earned_qty         numeric(18,4)
        GENERATED ALWAYS AS (round(actual_pct_complete / 100.0 * budget_qty, 4)) STORED,

    CONSTRAINT uq_fact_pid UNIQUE (project_id, id),
    CONSTRAINT uq_fact_key UNIQUE (project_id, cost_centre_id, reporting_period_id),
    CONSTRAINT fk_fact_cc FOREIGN KEY (project_id, cost_centre_id)
        REFERENCES qs.cost_centres (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_fact_rp FOREIGN KEY (project_id, reporting_period_id)
        REFERENCES qs.reporting_periods (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_fact_bl FOREIGN KEY (project_id, baseline_id)
        REFERENCES qs.cost_centre_baselines (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_fact_ev FOREIGN KEY (project_id, estimate_version_id)
        REFERENCES qs.estimate_versions (project_id, id) ON DELETE RESTRICT
);

-- ── Append-only cost ledger (Phase 3 — defined now, unused until cutover; Choice 2) ──
CREATE TABLE qs.period_cost_deltas (
    id                 bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id         bigint NOT NULL,
    cost_centre_id     bigint NOT NULL,
    reporting_period_id bigint NOT NULL,
    rtype              text   NOT NULL CHECK (rtype IN ('MANPOWER','MATERIAL','EQUIPMENT','SUBCONTRACT')),
    amount             numeric(18,2) NOT NULL,
    direction          text   NOT NULL CHECK (direction IN ('POSTING','REVERSAL')),
    idempotency_key    text   NOT NULL,
    posted_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_ledger_pid UNIQUE (project_id, id),
    CONSTRAINT uq_ledger_idem UNIQUE (project_id, idempotency_key),
    CONSTRAINT fk_ledger_cc FOREIGN KEY (project_id, cost_centre_id)
        REFERENCES qs.cost_centres (project_id, id) ON DELETE RESTRICT,
    CONSTRAINT fk_ledger_rp FOREIGN KEY (project_id, reporting_period_id)
        REFERENCES qs.reporting_periods (project_id, id) ON DELETE RESTRICT
);

-- ── Indexes on FK / access paths, project_id leading (plan §7) ─────────────────
CREATE INDEX ix_membership_user       ON qs.project_memberships (user_id);
CREATE INDEX ix_rp_project            ON qs.reporting_periods (project_id, period_id);
CREATE INDEX ix_ev_project            ON qs.estimate_versions (project_id, status);
CREATE INDEX ix_norms_version         ON qs.norms (project_id, estimate_version_id);
CREATE INDEX ix_normmat_norm          ON qs.norm_materials (project_id, norm_id);
CREATE INDEX ix_pkg_version           ON qs.estimate_packages (project_id, estimate_version_id);
CREATE INDEX ix_boq_version           ON qs.boq_items (project_id, estimate_version_id);
CREATE INDEX ix_boq_norm              ON qs.boq_items (project_id, norm_id);
CREATE INDEX ix_map_version           ON qs.boq_norm_mappings (project_id, estimate_version_id);
CREATE INDEX ix_map_boq               ON qs.boq_norm_mappings (project_id, boq_item_id);
CREATE INDEX ix_rl_version            ON qs.estimate_resource_lines (project_id, estimate_version_id);
CREATE INDEX ix_rl_boq                ON qs.estimate_resource_lines (project_id, boq_item_id);
CREATE INDEX ix_cc_project            ON qs.cost_centres (project_id, bcc_id);
CREATE INDEX ix_cc_pkg                ON qs.cost_centres (project_id, estimate_package_id);
CREATE INDEX ix_bl_version            ON qs.cost_centre_baselines (project_id, estimate_version_id);
CREATE INDEX ix_bl_cc                 ON qs.cost_centre_baselines (project_id, cost_centre_id);
CREATE INDEX ix_plan_key              ON qs.cost_centre_plan_periods (project_id, estimate_version_id, cost_centre_id, reporting_period_id);
CREATE INDEX ix_fact_cc               ON qs.cost_centre_periods (project_id, cost_centre_id, reporting_period_id);
CREATE INDEX ix_fact_rp               ON qs.cost_centre_periods (project_id, reporting_period_id);
CREATE INDEX ix_ledger_cc            ON qs.period_cost_deltas (project_id, cost_centre_id, reporting_period_id);
