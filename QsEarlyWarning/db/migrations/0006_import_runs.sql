-- 0006_import_runs.sql — import provenance + backfill-role validation grants (plan §5c, Finding 8)
--
-- The importer loads into the live tables inside ONE transaction (validate-before-commit is the
-- staging boundary; the previous active version is untouched until commit and preserved after).
-- import_runs records provenance: source file + hash, importer version, actor, row counts, status.

SET search_path = qs, public;

CREATE TABLE qs.import_runs (
    id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    project_id       bigint NOT NULL REFERENCES qs.projects(id) ON DELETE RESTRICT,
    source_file      text   NOT NULL,
    source_hash      text   NOT NULL,
    importer_version text   NOT NULL,
    actor            text   NOT NULL,
    status           text   NOT NULL CHECK (status IN ('running','activated','failed')),
    row_counts       jsonb,
    message          text,
    started_at       timestamptz NOT NULL DEFAULT now(),
    finished_at      timestamptz,
    CONSTRAINT uq_import_run_pid UNIQUE (project_id, id)
);
CREATE INDEX ix_import_runs_project ON qs.import_runs (project_id, started_at);

GRANT SELECT, INSERT, UPDATE, DELETE ON qs.import_runs TO qs_app, qs_worker, qs_bypass;

ALTER TABLE qs.import_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE qs.import_runs FORCE  ROW LEVEL SECURITY;
CREATE POLICY p_import_runs ON qs.import_runs
    USING (project_id = qs.fn_current_project_id() AND qs.fn_is_member(project_id))
    WITH CHECK (project_id = qs.fn_current_project_id() AND qs.fn_is_member(project_id));

-- The backfill/import role (qs_bypass) validates the estimate graph before activating it.
GRANT EXECUTE ON FUNCTION qs.fn_validate_publish(bigint, bigint)      TO qs_bypass;
GRANT EXECUTE ON FUNCTION qs.fn_validate_period_close(bigint, bigint) TO qs_bypass;
