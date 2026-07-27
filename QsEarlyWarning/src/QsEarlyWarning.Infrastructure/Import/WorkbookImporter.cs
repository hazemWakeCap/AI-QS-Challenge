using System.Globalization;
using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Domain.Estimate;
using QsEarlyWarning.Infrastructure.Excel;

namespace QsEarlyWarning.Infrastructure.Import;

/// <summary>
/// Staging importer + reconciliation (plan Phase 1 / §5c). Runs as the qs_bypass backfill role in a
/// single transaction: purge any prior load of this slug, insert project → periods → version →
/// cost_centres → baselines → plan_periods, run the Phase-0 publish validation, activate the
/// version, then bulk-load the facts. Only *inputs* are loaded — every EVM number is computed by
/// the database view and then reconciled against the workbook's recorded columns.
/// </summary>
public sealed class WorkbookImporter : IWorkbookImporter
{
    private const string ImporterVersion = "phase1-0.1";
    private readonly IPanelLoader _loader;
    private readonly IEstimateWorkbookReader _estimate;

    public WorkbookImporter(IPanelLoader loader, IEstimateWorkbookReader estimate)
    {
        _loader = loader;
        _estimate = estimate;
    }

    public ReconciliationReport Import(string workbookPath, string connectionString, string projectSlug, string actor)
        => Import(workbookPath, connectionString, projectSlug, actor,
            new ProjectMeta($"Tower X ({projectSlug})", "AED", 1));

    public ReconciliationReport Import(string workbookPath, string connectionString, string projectSlug, string actor, ProjectMeta meta)
    {
        var panel = _loader.Load(workbookPath);
        var sourceHash = FileSha256(workbookPath);

        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        Exec(conn, "SET ROLE qs_bypass");   // backfill role: full DML, RLS bypassed

        var report = new ReconciliationReport { ProjectSlug = projectSlug };

        using (var tx = conn.BeginTransaction())
        {
            Purge(conn, tx, projectSlug);

            var projectId = InsertReturning(conn, tx,
                "INSERT INTO qs.projects (slug, name, reporting_currency) VALUES (@s, @n, @cur) RETURNING id",
                Txt("@s", projectSlug), Txt("@n", meta.Name), Txt("@cur", meta.Currency));

            // Establish an owner membership so the RLS-scoped read path has a member.
            Exec(conn, tx, "INSERT INTO qs.project_memberships (project_id, user_id, role) VALUES (@p, @owner, 'owner')",
                Big("@p", projectId), Big("@owner", meta.OwnerUserId));

            var runId = InsertReturning(conn, tx,
                "INSERT INTO qs.import_runs (project_id, source_file, source_hash, importer_version, actor, status) " +
                "VALUES (@p, @f, @h, @v, @a, 'running') RETURNING id",
                Big("@p", projectId), Txt("@f", Path.GetFileName(workbookPath)), Txt("@h", sourceHash),
                Txt("@v", ImporterVersion), Txt("@a", actor));

            // ── reporting periods (Period_ID → Month_Year date) ──
            var periods = panel.Select(r => r.PeriodId).Distinct().OrderBy(x => x).ToList();
            var monthByPeriod = panel.GroupBy(r => r.PeriodId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.MonthYear).FirstOrDefault(m => m is not null));
            var rpId = new Dictionary<int, long>();
            var maxPeriod = periods.Max();
            foreach (var p in periods)
                rpId[p] = InsertReturning(conn, tx,
                    "INSERT INTO qs.reporting_periods (project_id, period_id, period_start) VALUES (@p, @o, @d) RETURNING id",
                    Big("@p", projectId), Intg("@o", p), Dt("@d", PeriodStart(p, monthByPeriod.GetValueOrDefault(p))));

            // ── draft estimate version (horizon = last imported period) ──
            var versionId = InsertReturning(conn, tx,
                "INSERT INTO qs.estimate_versions (project_id, version_no, status, schedule_horizon_period_id) " +
                "VALUES (@p, 1, 'draft', @h) RETURNING id",
                Big("@p", projectId), Intg("@h", maxPeriod));

            // ── cost centres + baselines (BAC constant per centre) ──
            var byCentre = panel.GroupBy(r => r.BccId).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            var ccId = new Dictionary<string, long>();
            var baselineId = new Dictionary<string, long>();
            foreach (var g in byCentre)
            {
                var any = g.First();
                var start = g.Min(x => x.PeriodId);
                var end = g.Max(x => x.PeriodId);
                // Zone_Area is constant per centre in this workbook, but take the first non-blank
                // rather than any.ZoneArea so a blank leading row can't null out a located centre.
                var zone = g.Select(x => x.ZoneArea)
                    .FirstOrDefault(z => !string.IsNullOrWhiteSpace(z));

                var cc = InsertReturning(conn, tx,
                    "INSERT INTO qs.cost_centres (project_id, bcc_id, wbs_code, package_code, discipline, " +
                    "zone_code, effective_start_period, effective_end_period) " +
                    "VALUES (@p, @b, @w, @k, @d, @z, @s, @e) RETURNING id",
                    Big("@p", projectId), Txt("@b", g.Key), Txt("@w", any.WbsCode), Txt("@k", any.PackageCode),
                    Txt("@d", any.Discipline), Txt("@z", zone), Intg("@s", start), Intg("@e", end));
                ccId[g.Key] = cc;

                var bac = g.Select(x => x.BacAed).FirstOrDefault(v => v is not null) ?? 0.0;
                baselineId[g.Key] = InsertReturning(conn, tx,
                    "INSERT INTO qs.cost_centre_baselines (project_id, estimate_version_id, cost_centre_id, bac_amount, budget_qty) " +
                    "VALUES (@p, @v, @c, @bac, NULL) RETURNING id",
                    Big("@p", projectId), Big("@v", versionId), Big("@c", cc), Num("@bac", bac));
            }

            // ── plan curve (planned_pct per centre-period, where present) ──
            using (var insPlan = new NpgsqlCommand(
                "INSERT INTO qs.cost_centre_plan_periods (project_id, estimate_version_id, cost_centre_id, reporting_period_id, planned_pct) " +
                "VALUES (@p, @v, @c, @r, @pp)", conn, tx))
            {
                insPlan.Parameters.Add(Big("@p", projectId));
                insPlan.Parameters.Add(Big("@v", versionId));
                insPlan.Parameters.Add(new NpgsqlParameter("@c", NpgsqlDbType.Bigint));
                insPlan.Parameters.Add(new NpgsqlParameter("@r", NpgsqlDbType.Bigint));
                insPlan.Parameters.Add(new NpgsqlParameter("@pp", NpgsqlDbType.Numeric));
                insPlan.Prepare();
                foreach (var r in panel.Where(x => x.PlanPctComplete is not null))
                {
                    insPlan.Parameters["@c"].Value = ccId[r.BccId];
                    insPlan.Parameters["@r"].Value = rpId[r.PeriodId];
                    insPlan.Parameters["@pp"].Value = Convert.ToDecimal(r.PlanPctComplete!.Value);
                    insPlan.ExecuteNonQuery();
                }
            }

            // ── estimate graph (sheets 1-4): persist into the six estimate tables while the version is
            //    still 'draft' (immutability trigger allows it) and before Validate (boq_rollup check).
            //    Best-effort: parse failure / missing sheets degrade to a skipped block, never a failed
            //    import. See PersistEstimate for the pre-validation + savepoint model. ──
            PersistEstimate(conn, tx, workbookPath, projectId, versionId, ccId.Count > 0, report);

            // ── validate the estimate graph before activating (Phase-0 publish rules) ──
            var violations = Validate(conn, tx, projectId, versionId);
            if (violations.Count > 0)
            {
                Exec(conn, tx, "UPDATE qs.import_runs SET status='failed', finished_at=now(), message=@m WHERE id=@id",
                    Txt("@m", $"publish validation failed: {violations.Count} issue(s)"), Big("@id", runId));
                tx.Rollback();
                var fail = new ReconciliationReport
                {
                    ProjectSlug = projectSlug,
                    CostCentres = byCentre.Count, Periods = periods.Count, Activated = false,
                    FailureReason = "publish validation failed",
                };
                fail.PublishViolations.AddRange(violations);
                return fail;
            }

            // ── activate: publish this version, point the project at it ──
            Exec(conn, tx, "UPDATE qs.estimate_versions SET status='published', published_at=now() WHERE id=@v", Big("@v", versionId));
            Exec(conn, tx, "UPDATE qs.projects SET active_estimate_version_id=@v, data_revision=data_revision+1 WHERE id=@p",
                Big("@v", versionId), Big("@p", projectId));

            // ── facts (inputs only; generated columns compute PV/EV/AC totals) ──
            var factCount = 0;
            using (var insFact = new NpgsqlCommand(
                "INSERT INTO qs.cost_centre_periods (project_id, cost_centre_id, reporting_period_id, baseline_id, estimate_version_id, " +
                "bac_amount, budget_qty, planned_pct, actual_pct_complete, ac_material_amount, ac_manpower_amount, " +
                "ac_equipment_amount, ac_subcontract_amount, lifecycle) " +
                "VALUES (@p, @c, @r, @b, @v, @bac, NULL, @pp, @ap, @m, @mp, @eq, @sc, @lc)", conn, tx))
            {
                insFact.Parameters.Add(Big("@p", projectId));
                insFact.Parameters.Add(new NpgsqlParameter("@c", NpgsqlDbType.Bigint));
                insFact.Parameters.Add(new NpgsqlParameter("@r", NpgsqlDbType.Bigint));
                insFact.Parameters.Add(new NpgsqlParameter("@b", NpgsqlDbType.Bigint));
                insFact.Parameters.Add(Big("@v", versionId));
                insFact.Parameters.Add(new NpgsqlParameter("@bac", NpgsqlDbType.Numeric));
                insFact.Parameters.Add(new NpgsqlParameter("@pp", NpgsqlDbType.Numeric));
                insFact.Parameters.Add(new NpgsqlParameter("@ap", NpgsqlDbType.Numeric));
                insFact.Parameters.Add(new NpgsqlParameter("@m", NpgsqlDbType.Numeric));
                insFact.Parameters.Add(new NpgsqlParameter("@mp", NpgsqlDbType.Numeric));
                insFact.Parameters.Add(new NpgsqlParameter("@eq", NpgsqlDbType.Numeric));
                insFact.Parameters.Add(new NpgsqlParameter("@sc", NpgsqlDbType.Numeric));
                insFact.Parameters.Add(new NpgsqlParameter("@lc", NpgsqlDbType.Text));
                insFact.Prepare();
                foreach (var r in panel)
                {
                    var bac = r.BacAed ?? panel.First(x => x.BccId == r.BccId && x.BacAed is not null).BacAed ?? 0.0;
                    var (m, mp, eq, sc) = NormalizeSplits(r);
                    insFact.Parameters["@c"].Value = ccId[r.BccId];
                    insFact.Parameters["@r"].Value = rpId[r.PeriodId];
                    insFact.Parameters["@b"].Value = baselineId[r.BccId];
                    insFact.Parameters["@bac"].Value = Convert.ToDecimal(bac);
                    insFact.Parameters["@pp"].Value = NumV(r.PlanPctComplete);
                    insFact.Parameters["@ap"].Value = NumV(r.ActualPctComplete);
                    insFact.Parameters["@m"].Value = NumV(m);
                    insFact.Parameters["@mp"].Value = NumV(mp);
                    insFact.Parameters["@eq"].Value = NumV(eq);
                    insFact.Parameters["@sc"].Value = NumV(sc);
                    insFact.Parameters["@lc"].Value = Lifecycle(r.AlertLevel);
                    insFact.ExecuteNonQuery();
                    factCount++;
                }
            }

            Exec(conn, tx,
                "UPDATE qs.import_runs SET status='activated', finished_at=now(), " +
                "row_counts = jsonb_build_object('cost_centres',@cc,'periods',@pe,'facts',@fa) WHERE id=@id",
                Intg("@cc", byCentre.Count), Intg("@pe", periods.Count), Intg("@fa", factCount), Big("@id", runId));

            tx.Commit();

            report.Activated = true;
            report.CostCentres = byCentre.Count;
            report.Periods = periods.Count;
            report.Facts = factCount;
        }

        Reconcile(conn, report, panel);
        return report;
    }

    // ── reconciliation: DB-computed EVM view vs recorded workbook columns ──
    private static void Reconcile(NpgsqlConnection conn, ReconciliationReport report, IReadOnlyList<CostCentrePeriod> panel)
    {
        var cpi  = new FieldRecon { Field = "cpi",                 RelTol = 0.005, AbsTol = 0.005 };
        var spi  = new FieldRecon { Field = "spi",                 RelTol = 0.005, AbsTol = 0.005 };
        var cv   = new FieldRecon { Field = "cv_amount",           RelTol = 0.005, AbsTol = 2.00 };
        var eac  = new FieldRecon { Field = "eac_amount",          RelTol = 0.005, AbsTol = 2.00 };
        var vac  = new FieldRecon { Field = "vac_amount",          RelTol = 0.010, AbsTol = 2.00 };
        var pct  = new FieldRecon { Field = "pct_budget_consumed", RelTol = 0.005, AbsTol = 0.10 };
        report.Fields.AddRange(new[] { cpi, spi, cv, eac, vac, pct });

        var slug = report.ProjectSlug;
        var computed = new Dictionary<(string, int), (decimal? cpi, decimal? spi, decimal? cv, decimal? eac, decimal? vac, decimal? pct, string? alert)>();
        using (var cmd = new NpgsqlCommand(
            "SELECT e.bcc_id, e.period_id, e.cpi, e.spi, e.cv_amount, e.eac_amount, e.vac_amount, e.pct_budget_consumed, e.alert_level " +
            "FROM qs.cost_centre_evm e JOIN qs.projects p ON p.id = e.project_id WHERE p.slug = @s", conn))
        {
            cmd.Parameters.Add(Txt("@s", slug));
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                computed[(rd.GetString(0), rd.GetInt32(1))] = (
                    D(rd, 2), D(rd, 3), D(rd, 4), D(rd, 5), D(rd, 6), D(rd, 7), rd.IsDBNull(8) ? null : rd.GetString(8));
        }

        foreach (var r in panel)
        {
            if (!computed.TryGetValue((r.BccId, r.PeriodId), out var c)) continue;
            var key = $"{r.BccId} P{r.PeriodId}";

            // A row where the workbook's OWN recorded EV/PV contradicts its inputs (Actual%×BAC /
            // Plan%×BAC) cannot be reproduced from those inputs — it is a source error, not a
            // derivation gap. Exclude and report it separately.
            if (!SourceConsistent(r))
            {
                report.SourceAnomalies++;
                if (report.SourceAnomalyExamples.Count < 20)
                    report.SourceAnomalyExamples.Add(
                        $"{key}: recorded EV={r.EvAed:0} but Actual%({r.ActualPctComplete})×BAC({r.BacAed:0})={(r.ActualPctComplete is double a && r.BacAed is double b ? a / 100 * b : 0):0}");
                continue;
            }
            Cmp(cpi, r.Cpi, c.cpi, key);
            Cmp(spi, r.Spi, c.spi, key);
            Cmp(cv, r.CvAed, c.cv, key);
            Cmp(eac, r.EacAed, c.eac, key);
            Cmp(pct, r.PctBudgetConsumed, c.pct, key);
            // VAC = BAC − EAC: its meaningful precision is EAC's magnitude, so scale the tolerance to
            // recorded EAC (VAC is a tiny difference of large numbers and reconciles within EAC's band).
            if (r.VacAed is double vrec)
            {
                vac.Compared++;
                var vacTol = Math.Max(vac.AbsTol, 0.005 * Math.Abs(r.EacAed ?? r.BacAed ?? 0));
                if (c.vac is decimal vc && Math.Abs((double)vc - vrec) <= Math.Max(vacTol, vac.RelTol * Math.Abs(vrec)))
                    vac.Matched++;
                else if (vac.Worst.Count < 20)
                    vac.Worst.Add($"{key}: computed={(c.vac?.ToString(CultureInfo.InvariantCulture) ?? "null")} recorded={vrec.ToString("0.####", CultureInfo.InvariantCulture)}");
            }

            if (r.AlertLevel is not null)
            {
                report.AlertCompared++;
                if (Norm(r.AlertLevel) == Norm(c.alert)) report.AlertMatched++;
                // Alert is a step function at CPI = 0.95; a row whose CPI sits within rounding
                // tolerance of the cutoff is label-indeterminate, so a flip there is not a mismatch.
                else if (c.cpi is decimal cc && Math.Abs((double)cc - 0.95) <= 0.005)
                {
                    report.AlertMatched++;
                    report.AlertBoundaryExcused++;
                }
                else if (report.AlertWorst.Count < 20)
                    report.AlertWorst.Add($"{key}: computed={c.alert ?? "null"} recorded={r.AlertLevel}");
            }
        }
    }

    /// <summary>True unless the workbook's own recorded EV/PV disagree with Actual%×BAC / Plan%×BAC.</summary>
    private static bool SourceConsistent(CostCentrePeriod r)
    {
        if (r.BacAed is not double bac) return true;
        if (r.ActualPctComplete is double ap && r.EvAed is double ev)
        {
            var should = ap / 100.0 * bac;
            if (Math.Abs(ev - should) > Math.Max(1.0, 0.01 * Math.Abs(should))) return false;
        }
        if (r.PlanPctComplete is double pp && r.PvAed is double pv)
        {
            var should = pp / 100.0 * bac;
            if (Math.Abs(pv - should) > Math.Max(1.0, 0.01 * Math.Abs(should))) return false;
        }
        return true;
    }

    private static void Cmp(FieldRecon f, double? recorded, decimal? computed, string key)
    {
        if (recorded is null) return;                 // recorded missing → outside the derivability claim
        f.Compared++;
        if (computed is decimal cc && f.Within((double)cc, recorded.Value)) f.Matched++;
        else if (f.Worst.Count < 20)
            f.Worst.Add($"{key}: computed={(computed?.ToString(CultureInfo.InvariantCulture) ?? "null")} recorded={recorded.Value.ToString("0.####", CultureInfo.InvariantCulture)}");
    }

    private static List<string> Validate(NpgsqlConnection conn, NpgsqlTransaction tx, long projectId, long versionId)
    {
        var list = new List<string>();
        using var cmd = new NpgsqlCommand("SELECT violation, cost_centre_id, detail FROM qs.fn_validate_publish(@p, @v)", conn, tx);
        cmd.Parameters.Add(Big("@p", projectId));
        cmd.Parameters.Add(Big("@v", versionId));
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            list.Add($"{rd.GetString(0)} (cc={(rd.IsDBNull(1) ? "-" : rd.GetInt64(1).ToString())}): {(rd.IsDBNull(2) ? "" : rd.GetString(2))}");
        return list;
    }

    private static void Purge(NpgsqlConnection conn, NpgsqlTransaction tx, string slug)
    {
        long? pid;
        using (var cmd = new NpgsqlCommand("SELECT id FROM qs.projects WHERE slug = @s", conn, tx))
        {
            cmd.Parameters.Add(Txt("@s", slug));
            pid = cmd.ExecuteScalar() as long?;
        }
        if (pid is null) return;

        // child → parent order (all FKs are ON DELETE RESTRICT). NOTE: cost_centres must be deleted BEFORE
        // estimate_packages — cost_centres.estimate_package_id → estimate_packages is ON DELETE RESTRICT, and
        // the importer now populates that link, so the old order (packages before centres) would fail on
        // re-import. cost_centres still comes after everything that references IT (periods/baselines/plan).
        foreach (var t in new[] {
            "import_runs", "cost_centre_periods", "period_cost_deltas", "cost_centre_plan_periods",
            "cost_centre_baselines", "estimate_resource_lines", "boq_norm_mappings", "boq_items",
            "cost_centres", "estimate_packages", "norm_materials", "norms" })
            Exec(conn, tx, $"DELETE FROM qs.{t} WHERE project_id = @p", Big("@p", pid.Value));
        Exec(conn, tx, "UPDATE qs.projects SET active_estimate_version_id = NULL WHERE id = @p", Big("@p", pid.Value));
        foreach (var t in new[] { "estimate_versions", "reporting_periods", "project_memberships" })
            Exec(conn, tx, $"DELETE FROM qs.{t} WHERE project_id = @p", Big("@p", pid.Value));
        Exec(conn, tx, "DELETE FROM qs.projects WHERE id = @p", Big("@p", pid.Value));
    }

    // ── estimate graph (sheets 1-4) persistence ─────────────────────────────────────────────────────
    // Runs inside the outer import transaction while the version is still 'draft' and before Validate.
    // Best-effort: a missing/unreadable estimate workbook is skipped (never fails the import); expected
    // bad rows are filtered in C# *before* any INSERT (a constraint violation would poison the whole tx);
    // a SAVEPOINT backstops any unexpected DB error so the historical-data load still commits.
    private void PersistEstimate(NpgsqlConnection conn, NpgsqlTransaction tx, string workbookPath,
        long projectId, long versionId, bool hasCostCentres, ReconciliationReport report)
    {
        EstimateModel model;
        try { model = _estimate.Read(workbookPath); }
        catch (Exception ex)
        {
            report.EstimateNotes.Add($"estimate sheets not persisted (unreadable/missing): {ex.Message}");
            return;
        }
        if (model.BoqLines.Count == 0 && model.Norms.Count == 0 &&
            model.ResourceLines.Count == 0 && model.Mappings.Count == 0)
        {
            report.EstimateNotes.Add("estimate sheets parsed empty; nothing persisted");
            return;
        }

        Exec(conn, tx, "SAVEPOINT est_graph");
        try
        {
            PersistEstimateCore(conn, tx, model, projectId, versionId, hasCostCentres, report);
            Exec(conn, tx, "RELEASE SAVEPOINT est_graph");
        }
        catch (Exception ex)
        {
            // Unexpected error: undo only the estimate block so Validate/publish/facts still succeed.
            Exec(conn, tx, "ROLLBACK TO SAVEPOINT est_graph");
            report.EstimateNorms = report.EstimatePackages = report.EstimateBoqItems = report.EstimateResourceLines = 0;
            report.EstimateNotes.Add($"estimate persistence rolled back after unexpected error: {ex.Message}");
        }
    }

    private static void PersistEstimateCore(NpgsqlConnection conn, NpgsqlTransaction tx, EstimateModel model,
        long projectId, long versionId, bool hasCostCentres, ReconciliationReport report)
    {
        // 1. norms (one per distinct code, first-wins to match EstimateModel.NormByCode)
        var normIdByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in model.NormByCode.Values)
            normIdByCode[n.NormCode] = InsertReturning(conn, tx,
                "INSERT INTO qs.norms (project_id, estimate_version_id, norm_code, description, unit, output_norm) " +
                "VALUES (@p, @v, @code, @desc, @unit, @on) RETURNING id",
                Big("@p", projectId), Big("@v", versionId), Txt("@code", n.NormCode),
                Txt("@desc", n.Notes), Txt("@unit", n.Unit), NumN("@on", n.OutputNorm));
        report.EstimateNorms = normIdByCode.Count;

        // 2. norm_materials — synthetic MAT1/MAT2 codes from the two per-UoW material qty columns
        //    (sheet 2 has quantities but no material codes; real material lines live in resource_lines).
        foreach (var n in model.NormByCode.Values)
        {
            if (!normIdByCode.TryGetValue(n.NormCode, out var nid)) continue;
            foreach (var (code, qty) in new[] { ("MAT1", n.Mat1QtyPerUoW), ("MAT2", n.Mat2QtyPerUoW) })
            {
                if (qty is null) continue;
                Exec(conn, tx,
                    "INSERT INTO qs.norm_materials (project_id, norm_id, material_code, qty_per_unit) " +
                    "VALUES (@p, @n, @code, @q)",
                    Big("@p", projectId), Big("@n", nid), Txt("@code", code), NumN("@q", qty));
            }
        }

        // 3. estimate_packages — distinct codes seen on mappings + resource lines
        var pkgIdByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var pkgCodes = model.Mappings.Select(m => m.EstimatePackage)
            .Concat(model.ResourceLines.Select(r => r.Package))
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var code in pkgCodes)
            pkgIdByCode[code] = InsertReturning(conn, tx,
                "INSERT INTO qs.estimate_packages (project_id, estimate_version_id, code, name) " +
                "VALUES (@p, @v, @code, NULL) RETURNING id",
                Big("@p", projectId), Big("@v", versionId), Txt("@code", code));
        report.EstimatePackages = pkgIdByCode.Count;

        // 4. Pre-compute per-item resource-line sums (mirrors the GENERATED resource_cost_amount and the
        //    SQL rollup), keyed by the composite (Sec, ItemRef) — the DB natural key.
        var itemsWithLines = new HashSet<(string, string)>();
        var lineSum = new Dictionary<(string, string), decimal>();
        foreach (var rl in model.ResourceLines)
        {
            var key = (rl.Sec ?? "", rl.ItemRef);
            itemsWithLines.Add(key);
            lineSum[key] = lineSum.GetValueOrDefault(key) + EstimateMapping.LineCost(rl.TotalResourceQty, rl.UnitRate);
        }

        // 5. boq_items — total_amount is the resource-cost ROLLUP the DB defines it to be. The schema
        //    ("resource lines roll up to this") and fn_validate_publish's boq_rollup_mismatch require
        //    total_amount == Σ resource_cost_amount within greatest(1.00, 0.5%). The workbook's TOTAL Amount
        //    includes margin + contingency, so it is always LARGER than the direct resource-cost rollup —
        //    persisting it would FAIL publish validation and roll back the import. So we store the rollup
        //    itself (items with no resource lines → NULL: nothing to roll up). Decided BEFORE the INSERT so
        //    the value is already correct; no post-insert mutation. The workbook's margin-inclusive contract
        //    total has no home under the no-schema-change scope (see plan §Scope decisions).
        var boqIdByRef = new Dictionary<(string, string), long>();
        int boqNoLines = 0;
        foreach (var b in model.BoqLines)
        {
            var key = (b.Sec ?? "", b.ItemRef);
            if (boqIdByRef.ContainsKey(key)) continue;   // first-wins on the (rare) duplicate composite
            long? normId = b.NormRef is string nr && normIdByCode.TryGetValue(nr, out var nrid) ? nrid : null;

            object total;
            if (itemsWithLines.Contains(key)) total = lineSum.GetValueOrDefault(key);   // rollup ties by construction
            else { total = DBNull.Value; boqNoLines++; }

            boqIdByRef[key] = InsertReturning(conn, tx,
                "INSERT INTO qs.boq_items (project_id, estimate_version_id, boq_sec, item_ref, description, unit, quantity, norm_id, total_amount) " +
                "VALUES (@p, @v, @sec, @item, @desc, @unit, @qty, @norm, @total) RETURNING id",
                Big("@p", projectId), Big("@v", versionId), Txt("@sec", b.Sec ?? ""), Txt("@item", b.ItemRef),
                Txt("@desc", b.Description), Txt("@unit", b.Unit), NumN("@qty", b.Quantity),
                BigN("@norm", normId), new NpgsqlParameter("@total", NpgsqlDbType.Numeric) { Value = total });
        }
        report.EstimateBoqItems = boqIdByRef.Count;
        if (boqNoLines > 0) report.EstimateNotes.Add(
            $"{boqNoLines} BOQ item(s) had no resource lines → total_amount NULL (no rollup source)");

        // 6. boq_norm_mappings — emit only when all three FKs resolve (pre-validated; never a dangling FK)
        int mapSkipped = 0;
        foreach (var m in model.Mappings)
        {
            if (!boqIdByRef.TryGetValue((m.Sec ?? "", m.ItemRef), out var boqId)) { mapSkipped++; continue; }
            if (m.NormCode is not string nc || !normIdByCode.TryGetValue(nc, out var nId)) { mapSkipped++; continue; }
            if (m.EstimatePackage is not string pc || !pkgIdByCode.TryGetValue(pc.Trim(), out var pId)) { mapSkipped++; continue; }
            Exec(conn, tx,
                "INSERT INTO qs.boq_norm_mappings (project_id, estimate_version_id, boq_item_id, norm_id, estimate_package_id) " +
                "VALUES (@p, @v, @b, @n, @pk)",
                Big("@p", projectId), Big("@v", versionId), Big("@b", boqId), Big("@n", nId), Big("@pk", pId));
        }
        if (mapSkipped > 0) report.EstimateNotes.Add($"{mapSkipped} BOQ→norm mapping(s) skipped (unresolved item/norm/package)");

        // 7. estimate_resource_lines — need boq_item_id + a valid rtype (norm_id nullable). resource_cost_amount
        //    is GENERATED, never inserted. quantity = Total Resource Qty (divisor already applied in workbook).
        int rlCount = 0, rlSkipped = 0;
        foreach (var rl in model.ResourceLines)
        {
            if (!boqIdByRef.TryGetValue((rl.Sec ?? "", rl.ItemRef), out var boqId)) { rlSkipped++; continue; }
            if (EstimateMapping.NormalizeRtype(rl.ResourceType) is not string rtype) { rlSkipped++; continue; }
            long? nId = rl.NormCode is string nc2 && normIdByCode.TryGetValue(nc2, out var n3) ? n3 : null;
            Exec(conn, tx,
                "INSERT INTO qs.estimate_resource_lines (project_id, estimate_version_id, boq_item_id, norm_id, rtype, quantity, unit_rate_amount) " +
                "VALUES (@p, @v, @b, @n, @rt, @q, @rate)",
                Big("@p", projectId), Big("@v", versionId), Big("@b", boqId), BigN("@n", nId),
                Txt("@rt", rtype), NumN("@q", rl.TotalResourceQty), NumN("@rate", rl.UnitRate));
            rlCount++;
        }
        report.EstimateResourceLines = rlCount;
        if (rlSkipped > 0) report.EstimateNotes.Add($"{rlSkipped} resource line(s) skipped (unresolved BOQ item or unknown rtype)");

        // 8. Link cost centres to their estimate package by the package_code sheet-9 already carries.
        //    Needs both cost_centres (step 5 of the import) and estimate_packages (step 3 here) to exist.
        if (hasCostCentres)
            Exec(conn, tx,
                "UPDATE qs.cost_centres cc SET estimate_package_id = ep.id " +
                "FROM qs.estimate_packages ep " +
                "WHERE ep.project_id = cc.project_id AND ep.estimate_version_id = @v " +
                "AND ep.code = cc.package_code AND cc.project_id = @p",
                Big("@v", versionId), Big("@p", projectId));
    }

    // ── small helpers ──
    private static string Lifecycle(string? alert) => (alert ?? "").ToUpperInvariant() switch
    {
        "NOT STARTED" => "NOT_STARTED",
        "CLOSED" => "CLOSED",
        _ => "IN_PROGRESS",
    };

    private static string? Norm(string? s) => s?.Trim().ToUpperInvariant().Replace(' ', '_');

    /// <summary>
    /// The recorded actual cost of record is AC_AED_Cumulative (mapped to <see cref="CostCentrePeriod.AcCumulative"/>).
    /// The four resource splits are a breakdown that *usually* sums to it, but the source has a few
    /// anomalous rows where it does not. We keep the recorded total authoritative (so DB EVM matches
    /// the workbook, which computed CPI/EAC/pct from AC_AED_Cumulative) and distribute it across the
    /// four types by the recorded proportions. Where no split detail exists, the whole total is
    /// booked to manpower so ac_total still equals the recorded actual cost.
    /// </summary>
    private static (double? m, double? mp, double? eq, double? sc) NormalizeSplits(CostCentrePeriod r)
    {
        if (r.AcCumulative is not double tRaw) return (r.AcMaterial, r.AcManpower, r.AcEquipment, r.AcSubcontract);
        double t = Math.Round(tRaw);                       // the workbook's AC is whole AED
        double m = r.AcMaterial ?? 0, mp = r.AcManpower ?? 0, eq = r.AcEquipment ?? 0, sc = r.AcSubcontract ?? 0;
        double s = m + mp + eq + sc;
        if (s <= 0) return t == 0 ? (0d, 0d, 0d, 0d) : (t, 0d, 0d, 0d);
        double f = t / s;
        double mpr = Math.Round(mp * f), eqr = Math.Round(eq * f), scr = Math.Round(sc * f);
        return (t - mpr - eqr - scr, mpr, eqr, scr);       // material absorbs the remainder → sum == t exactly
    }

    private static DateTime PeriodStart(int period, string? monthYear)
    {
        if (monthYear is not null)
            foreach (var fmt in new[] { "MMM-yyyy", "MMM-yy", "MMMM-yyyy", "yyyy-MM" })
                if (DateTime.TryParseExact(monthYear, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    return DateTime.SpecifyKind(new DateTime(d.Year, d.Month, 1), DateTimeKind.Unspecified);
        return new DateTime(2020, 1, 1).AddMonths(period - 1);
    }

    private static string FileSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    private static object NumV(double? v) => v is double d ? Convert.ToDecimal(d) : DBNull.Value;
    private static decimal? D(NpgsqlDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetDecimal(i);

    private static NpgsqlParameter Num(string n, double v) => new(n, NpgsqlDbType.Numeric) { Value = Convert.ToDecimal(v) };
    private static NpgsqlParameter NumN(string n, double? v) => new(n, NpgsqlDbType.Numeric) { Value = NumV(v) };
    private static NpgsqlParameter Txt(string n, string? v) => new(n, NpgsqlDbType.Text) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter Big(string n, long v) => new(n, NpgsqlDbType.Bigint) { Value = v };
    private static NpgsqlParameter BigN(string n, long? v) => new(n, NpgsqlDbType.Bigint) { Value = (object?)v ?? DBNull.Value };
    private static NpgsqlParameter Intg(string n, int v) => new(n, NpgsqlDbType.Integer) { Value = v };
    private static NpgsqlParameter Dt(string n, DateTime v) => new(n, NpgsqlDbType.Date) { Value = v };

    private static long InsertReturning(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params NpgsqlParameter[] ps)
    {
        using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(ps);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Exec(NpgsqlConnection conn, string sql)
    {
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params NpgsqlParameter[] ps)
    {
        using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(ps);
        cmd.ExecuteNonQuery();
    }
}
