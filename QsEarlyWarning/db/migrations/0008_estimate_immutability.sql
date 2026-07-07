-- 0008_estimate_immutability.sql — published estimate graphs are immutable (plan §5.0 Choice 3;
-- Findings 3/10). Only a DRAFT version's authored rows may change; once published (or later
-- superseded) the graph is frozen, so closed-period facts that snapshotted it can never be rewritten
-- by re-authoring. To change a published estimate you author a NEW draft version and publish it.
--
-- The migration/purge path (a role with BYPASSRLS, or a superuser) is exempt — the importer purges
-- and reloads whole projects, and rebaseline/publish are managed by procedures.

SET search_path = qs, public;

CREATE OR REPLACE FUNCTION qs.trg_estimate_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$   -- SECURITY INVOKER on purpose: current_user must be the real caller
DECLARE v_ver bigint; v_status text;
BEGIN
    -- migration / purge / admin contexts are exempt
    IF (SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user) THEN
        RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
    END IF;

    v_ver := CASE WHEN TG_OP = 'DELETE' THEN OLD.estimate_version_id ELSE NEW.estimate_version_id END;
    SELECT status INTO v_status FROM qs.estimate_versions WHERE id = v_ver;
    IF v_status IS DISTINCT FROM 'draft' THEN
        RAISE EXCEPTION 'estimate version % is % and immutable; author a new draft version instead',
            v_ver, COALESCE(v_status, 'not visible') USING ERRCODE = '23514';
    END IF;
    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
END
$$;

-- Apply to every authored estimate-graph table (NOT the facts, which are period data).
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY[
        'norms','norm_materials','estimate_packages','boq_items','boq_norm_mappings',
        'estimate_resource_lines','cost_centre_baselines','cost_centre_plan_periods'
    ] LOOP
        EXECUTE format(
            'CREATE TRIGGER trg_estimate_immutable BEFORE INSERT OR UPDATE OR DELETE ON qs.%I '
            || 'FOR EACH ROW EXECUTE FUNCTION qs.trg_estimate_immutable()', t);
    END LOOP;
END
$$;
