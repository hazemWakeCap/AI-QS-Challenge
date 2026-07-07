#!/usr/bin/env bash
# apply.sh — apply the Phase-0 migrations, in order, to an existing database.
#
# Usage:  ./apply.sh [dbname]         (default db: qs_phase0)
# Env:    PGHOST (localhost) PGPORT (5432) PGUSER (superuser/createrole role)
#
# Roles are created by 0001 (needs a superuser or CREATEROLE connection). Objects 0002-0005 are
# created as qs_owner so the owner is never the app/worker role (plan §5.0 Choice 4).
set -euo pipefail

DB="${1:-qs_phase0}"
DIR="$(cd "$(dirname "$0")" && pwd)"
MIG="$DIR/migrations"
export PGHOST="${PGHOST:-localhost}"
export PGPORT="${PGPORT:-5432}"

# PostgreSQL 15+ is required for security_invoker views (plan §5.0 Choice 4 / Finding 2).
ver="$(psql -d postgres -tAc 'show server_version_num' | tr -d '[:space:]')"
if [ "${ver:-0}" -lt 150000 ]; then
  echo "ERROR: PostgreSQL 15+ required (security_invoker views). Found server_version_num=$ver" >&2
  exit 1
fi
echo "Postgres server_version_num=$ver (>= 150000 OK)"

psql -v ON_ERROR_STOP=1 -d "$DB" <<SQL
\echo == 0001 roles ==
\i $MIG/0001_roles.sql
GRANT CREATE ON DATABASE "$DB" TO qs_owner;
SET ROLE qs_owner;
\echo == 0002 schema ==
\i $MIG/0002_schema.sql
\echo == 0003 evm view ==
\i $MIG/0003_evm_view.sql
\echo == 0004 rls ==
\i $MIG/0004_rls.sql
\echo == 0005 procedures + triggers ==
\i $MIG/0005_procedures_triggers.sql
\echo == 0006 import runs ==
\i $MIG/0006_import_runs.sql
\echo == 0007 cost ledger ==
\i $MIG/0007_cost_ledger.sql
\echo == 0008 estimate immutability ==
\i $MIG/0008_estimate_immutability.sql
\echo == 0009 dashboard ==
\i $MIG/0009_dashboard.sql
RESET ROLE;
SQL

echo "OK: Phase-0 schema applied to database '$DB'."
