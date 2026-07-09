using System.Text;
using Npgsql;
using QsEarlyWarning.Infrastructure.Excel;
using QsEarlyWarning.Infrastructure.Import;
using Testcontainers.PostgreSql;

namespace QsEarlyWarning.Db.Tests;

/// <summary>
/// DB-backed guard for estimate-graph persistence (plan: "Persist the estimate sheets 1-4 during import").
/// Spins up a real PostgreSQL 17 container, applies the Phase-0 migrations, then drives the actual
/// <see cref="WorkbookImporter"/> against it — proving the things the DB-less mapping tests cannot:
/// NOT-NULL column coverage, composite-key FK resolution, the publish-time BOQ rollup, the
/// cost_centres → estimate_packages link, and above all the purge/re-import ordering fix (a SECOND import
/// of the same slug must succeed, which the pre-fix delete order would have failed on `fk_cc_pkg`).
///
/// Requires Docker; soft-skips when it is unavailable (same pattern as <see cref="Phase0GateTests"/>).
/// </summary>
public sealed class EstimateImportDbTests
{
    [Fact]
    public async Task Import_persists_estimate_graph_and_reimport_succeeds()
    {
        await using var pg = new PostgreSqlBuilder()
            .WithImage("postgres:17")
            .WithDatabase("qs_imp")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithResourceMapping(DbDirectory(), "/db")
            .Build();

        try { await pg.StartAsync(); }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            Console.WriteLine($"[SKIP] Docker not available; estimate-import DB test not run ({ex.GetType().Name}).");
            return;
        }

        // Apply the same migrations db/apply.sh applies (roles + schema + rls + procs + immutability + …).
        var apply = await pg.ExecAsync(new[]
        {
            // invoke via `bash <script>` (not direct exec) — WithResourceMapping copies without +x
            "bash", "-lc", "export PGUSER=postgres PGPASSWORD=postgres; bash /db/apply.sh qs_imp",
        });
        Assert.True(apply.ExitCode == 0, $"apply.sh failed (exit {apply.ExitCode})\nSTDOUT:\n{apply.Stdout}\nSTDERR:\n{apply.Stderr}");

        var connStr = pg.GetConnectionString();
        var workbook = WorkbookPath();
        var importer = new WorkbookImporter(new ExcelPanelLoader(), new EstimateWorkbookReader());

        // ── first import ──
        var r1 = importer.Import(workbook, connStr, "tower-x", "db-test");
        Assert.True(r1.Passed, $"first import did not pass:\n{r1.Render()}");
        Assert.True(r1.EstimateBoqItems > 0, "no boq_items persisted");
        Assert.True(r1.EstimateResourceLines > 0, "no resource lines persisted");
        Assert.True(r1.EstimateNorms > 0 && r1.EstimatePackages > 0, "no norms/packages persisted");

        // all six estimate tables non-zero (norm_materials via synthetic MAT1/MAT2)
        foreach (var t in new[] { "norms", "norm_materials", "estimate_packages", "boq_items",
                                  "boq_norm_mappings", "estimate_resource_lines" })
            Assert.True(await Count(connStr, $"qs.{t}") > 0, $"qs.{t} is empty after import");

        // cost centres linked to their estimate package
        Assert.True(await Count(connStr, "qs.cost_centres", "estimate_package_id is not null") > 0,
            "no cost_centres linked to an estimate package");

        // publish rollup reconciles (total_amount == Σ resource_cost_amount): no boq_rollup_mismatch
        Assert.Equal(0, await BoqRollupViolations(connStr));

        var boqAfterFirst = await Count(connStr, "qs.boq_items");

        // ── RE-IMPORT (same slug) — exercises the Purge reorder (cost_centres before estimate_packages).
        //    Against the old delete order this fails on fk_cc_pkg ON DELETE RESTRICT. ──
        var r2 = importer.Import(workbook, connStr, "tower-x", "db-test-2");
        Assert.True(r2.Passed, $"re-import did not pass (purge/re-import ordering?):\n{r2.Render()}");
        Assert.Equal(boqAfterFirst, await Count(connStr, "qs.boq_items"));   // no duplication, no leak
        Assert.Equal(1, await Count(connStr, "qs.projects", "slug = 'tower-x'"));
    }

    // ── small query helpers (superuser connection bypasses RLS) ──
    private static async Task<long> Count(string connStr, string table, string? where = null)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        var sql = $"SELECT count(*) FROM {table}" + (where is null ? "" : $" WHERE {where}");
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<int> BoqRollupViolations(string connStr)
    {
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM qs.fn_validate_publish(" +
            "(SELECT id FROM qs.projects WHERE slug='tower-x'), " +
            "(SELECT active_estimate_version_id FROM qs.projects WHERE slug='tower-x')) " +
            "WHERE violation = 'boq_rollup_mismatch'", conn);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private static bool IsDockerUnavailable(Exception ex)
    {
        var m = new StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException) m.Append(e.Message).Append(' ');
        var s = m.ToString();
        return s.Contains("Docker", StringComparison.OrdinalIgnoreCase)
            || s.Contains("daemon", StringComparison.OrdinalIgnoreCase)
            || s.Contains("pipe", StringComparison.OrdinalIgnoreCase)
            || s.Contains("socket", StringComparison.OrdinalIgnoreCase);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QsEarlyWarning.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("QsEarlyWarning.sln not found above test assembly.");
    }

    private static string DbDirectory()
    {
        var db = Path.Combine(RepoRoot(), "db");
        if (!Directory.Exists(db)) throw new DirectoryNotFoundException($"db/ not found at {db}");
        return db;
    }

    private static string WorkbookPath()
    {
        // db/ lives under <repo>/QsEarlyWarning; the workbook lives under <repo-root>/data.
        var dir = new DirectoryInfo(RepoRoot());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "Tower_X_Project_Data.xlsx");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate data/Tower_X_Project_Data.xlsx.");
    }
}
