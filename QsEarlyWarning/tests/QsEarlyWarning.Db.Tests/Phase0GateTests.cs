using System.Text;
using Testcontainers.PostgreSql;

namespace QsEarlyWarning.Db.Tests;

/// <summary>
/// Phase-0 exit gate, CI parity (plan §7). Spins up a real PostgreSQL 17 container and runs the
/// SAME migration + seed + contract SQL that db/run_tests.sh runs locally, via psql inside the
/// container. This is deliberately not a re-implementation: identical SQL proves the DDL, the RLS
/// boundary, snapshot immutability, and the publish/period-close validation in CI.
///
/// Requires Docker. When Docker is unavailable the fact is skipped (Testcontainers throws on start),
/// so local runs without a daemon do not hard-fail the build; CI runs with Docker enforce the gate.
/// </summary>
public sealed class Phase0GateTests
{
    [Fact]
    public async Task Migrations_apply_and_all_contract_tests_pass()
    {
        // postgres:17 default superuser is "postgres" — needed because 0001 creates roles.
        await using var pg = new PostgreSqlBuilder()
            .WithImage("postgres:17")
            .WithResourceMapping(DbDirectory(), "/db")
            .Build();

        try
        {
            await pg.StartAsync();
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            // xunit 2.5.x has no dynamic Assert.Skip; soft-return so a daemon-less dev box does not
            // hard-fail the build. CI provisions Docker, so the gate is enforced there.
            Console.WriteLine($"[SKIP] Docker not available; Phase-0 container gate not run ({ex.GetType().Name}).");
            return;
        }

        // run_tests.sh recreates the db, applies 0001-0005 as qs_owner, seeds, and runs the
        // contract suite. Inside the container psql connects over the local socket as postgres.
        var result = await pg.ExecAsync(new[] { "bash", "/db/run_tests.sh", "qs_ci" });

        var stdout = result.Stdout ?? string.Empty;
        var stderr = result.Stderr ?? string.Empty;
        Assert.True(result.ExitCode == 0,
            $"Phase-0 gate failed (exit {result.ExitCode}).\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.Contains("PHASE-0 GATE: PASS", stdout);
        Assert.Contains("ALL CONTRACT TESTS PASSED", stdout);
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

    /// <summary>Locate QsEarlyWarning/db by walking up from the test assembly to the .sln.</summary>
    private static string DbDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "QsEarlyWarning.sln")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("Could not locate QsEarlyWarning.sln above the test assembly.");
        var db = Path.Combine(dir.FullName, "db");
        if (!Directory.Exists(db)) throw new DirectoryNotFoundException($"db/ not found at {db}");
        return db;
    }
}
