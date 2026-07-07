-- 0001_roles.sql — role separation for the tenant boundary (plan §5.0 Choice 4, 3rd/4th codex review)
--
-- Four roles, each with a distinct trust level. RLS is a *table* mechanism, so the whole
-- design turns on who owns the tables and who can bypass row security:
--
--   qs_owner   — owns every object (tables, view, functions, procedures). Migrations run as
--                (or SET ROLE to) qs_owner. NOT granted to app/worker. Because tenant tables
--                are FORCE ROW LEVEL SECURITY, even the owner is subject to policy on them.
--   qs_app     — the request-path application role. NO BYPASSRLS, owns NO tenant tables,
--                subject to FORCE RLS. Gets DML on tenant tables but column-level UPDATE on
--                cost_centre_periods is later restricted to the mutable actual columns only.
--   qs_worker  — the async registry/importer service principal. Same constraints as qs_app
--                (no BYPASSRLS, owns nothing); it is itself RLS-governed and carries its own
--                project_memberships rows (a service-principal user_id). Not an anonymous bypass.
--   qs_bypass  — the ONLY role permitted to sidestep RLS, for migrations / purge / backfill.
--                Held by an operator, never by the app or worker.
--
-- Roles are cluster-global. Creation is idempotent so re-applying a migration is safe.
-- Passwords are intentionally omitted here; grant LOGIN + set credentials per environment.

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'qs_owner') THEN
    CREATE ROLE qs_owner NOLOGIN NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'qs_app') THEN
    CREATE ROLE qs_app NOLOGIN NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'qs_worker') THEN
    CREATE ROLE qs_worker NOLOGIN NOBYPASSRLS;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'qs_bypass') THEN
    -- The single role allowed to bypass row security. Migrations / purge only.
    CREATE ROLE qs_bypass NOLOGIN BYPASSRLS;
  END IF;
END
$$;

-- Belt-and-braces: guarantee the app/worker roles can never bypass RLS even if a
-- prior definition set it. (No-op when already NOBYPASSRLS.)
ALTER ROLE qs_app    NOBYPASSRLS;
ALTER ROLE qs_worker NOBYPASSRLS;
ALTER ROLE qs_owner  NOBYPASSRLS;

-- App and worker may connect to the schema and use sequences; object-level DML grants
-- are issued in 0004 alongside the RLS policies so privilege and policy live together.
