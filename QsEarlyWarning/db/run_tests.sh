#!/usr/bin/env bash
# run_tests.sh — recreate a throwaway database, apply Phase-0 migrations, seed, run contract tests.
# Env: PGHOST (localhost) PGPORT (5432) PGUSER (superuser/createrole). Exits non-zero on any failure.
set -euo pipefail

DB="${1:-qs_phase0_test}"
DIR="$(cd "$(dirname "$0")" && pwd)"
export PGHOST="${PGHOST:-localhost}"
export PGPORT="${PGPORT:-5432}"

echo "== recreate database $DB =="
psql -v ON_ERROR_STOP=1 -d postgres -c "DROP DATABASE IF EXISTS $DB;" -c "CREATE DATABASE $DB;"

echo "== apply migrations =="
"$DIR/apply.sh" "$DB" >/dev/null
echo "   migrations applied."

echo "== seed + contract tests =="
psql -v ON_ERROR_STOP=1 -d "$DB" -f "$DIR/tests/seed.sql" -f "$DIR/tests/test_contracts.sql"

echo "== cost-ledger tests (Phase 3) =="
psql -v ON_ERROR_STOP=1 -d "$DB" -f "$DIR/tests/test_ledger.sql"

echo "== estimate-authoring tests (Phase 4) =="
psql -v ON_ERROR_STOP=1 -d "$DB" -f "$DIR/tests/test_authoring.sql"

echo "== currency/FX integrity tests (Phase 5) =="
psql -v ON_ERROR_STOP=1 -d "$DB" -f "$DIR/tests/test_portfolio.sql"

echo ""
echo "PHASE-0 GATE: PASS"
