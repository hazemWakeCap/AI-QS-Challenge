using Npgsql;
using NpgsqlTypes;
using QsEarlyWarning.Infrastructure.Import;

namespace QsEarlyWarning.Infrastructure.Postgres;

/// <summary>Raised when creating a project whose slug is already taken.</summary>
public sealed class ProjectExistsException : Exception
{
    public ProjectExistsException(string message) : base(message) { }
}

/// <summary>
/// In-app project lifecycle outside the workbook importer: create an empty project, read/update its
/// metadata, and delete it. Runs as the <c>qs_bypass</c> backfill role (BYPASSRLS) — the same role the
/// importer uses — because creating the first membership and mutating <c>qs.projects</c> sit outside the
/// per-project RLS boundary. Authorization is enforced upstream in the controller (membership check).
/// </summary>
public sealed class ProjectAdminService
{
    private readonly string _connectionString;

    public ProjectAdminService(string connectionString) => _connectionString = connectionString;

    /// <summary>Insert a new project + owner membership with no data. Throws <see cref="ProjectExistsException"/>
    /// if the slug is taken.</summary>
    public async Task<long> CreateEmptyAsync(string slug, ProjectMeta meta, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "SET ROLE qs_bypass", ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var chk = new NpgsqlCommand("SELECT 1 FROM qs.projects WHERE slug = @s", conn, tx))
        {
            chk.Parameters.Add(Txt("@s", slug));
            if (await chk.ExecuteScalarAsync(ct) is not null)
                throw new ProjectExistsException($"Project '{slug}' already exists.");
        }

        long id;
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO qs.projects (slug, name, reporting_currency) VALUES (@s, @n, @cur) RETURNING id", conn, tx))
        {
            ins.Parameters.Add(Txt("@s", slug));
            ins.Parameters.Add(Txt("@n", meta.Name));
            ins.Parameters.Add(Txt("@cur", meta.Currency));
            id = (long)(await ins.ExecuteScalarAsync(ct))!;
        }
        await using (var mem = new NpgsqlCommand(
            "INSERT INTO qs.project_memberships (project_id, user_id, role) VALUES (@p, @u, 'owner')", conn, tx))
        {
            mem.Parameters.Add(Big("@p", id));
            mem.Parameters.Add(Big("@u", meta.OwnerUserId));
            await mem.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>Current name / currency / owner for a slug (owner = the earliest 'owner' membership),
    /// or null if the project does not exist. Used to preserve metadata across a re-import.</summary>
    public async Task<ProjectMeta?> GetMetaAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "SET ROLE qs_bypass", ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT p.name, p.reporting_currency, " +
            "  COALESCE((SELECT m.user_id FROM qs.project_memberships m " +
            "            WHERE m.project_id = p.id AND m.role = 'owner' ORDER BY m.user_id LIMIT 1), 1) " +
            "FROM qs.projects p WHERE p.slug = @s", conn);
        cmd.Parameters.Add(Txt("@s", slug));
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new ProjectMeta(rd.GetString(0), rd.GetString(1), rd.GetInt64(2));
    }

    /// <summary>Patch name and/or currency (nulls are left unchanged). Returns false if the slug is unknown.</summary>
    public async Task<bool> UpdateMetaAsync(string slug, string? name, string? currency, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "SET ROLE qs_bypass", ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE qs.projects SET name = COALESCE(@n, name), reporting_currency = COALESCE(@cur, reporting_currency) " +
            "WHERE slug = @s", conn);
        cmd.Parameters.Add(new NpgsqlParameter("@n", NpgsqlDbType.Text) { Value = (object?)name ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("@cur", NpgsqlDbType.Text) { Value = (object?)currency ?? DBNull.Value });
        cmd.Parameters.Add(Txt("@s", slug));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Delete a project and all its data (child → parent, mirroring the importer's purge).
    /// Returns false if the slug is unknown.</summary>
    public async Task<bool> DeleteAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "SET ROLE qs_bypass", ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        long? pid;
        await using (var cmd = new NpgsqlCommand("SELECT id FROM qs.projects WHERE slug = @s", conn, tx))
        {
            cmd.Parameters.Add(Txt("@s", slug));
            pid = await cmd.ExecuteScalarAsync(ct) as long?;
        }
        if (pid is null) { await tx.RollbackAsync(ct); return false; }

        // child → parent order (all FKs are ON DELETE RESTRICT) — same list/order as WorkbookImporter.Purge.
        // cost_centres MUST come before estimate_packages: cost_centres.estimate_package_id → estimate_packages
        // is ON DELETE RESTRICT and the importer now populates that link, so packages-before-centres would fail.
        foreach (var t in new[] {
            "import_runs", "cost_centre_periods", "period_cost_deltas", "cost_centre_plan_periods",
            "cost_centre_baselines", "estimate_resource_lines", "boq_norm_mappings", "boq_items",
            "cost_centres", "estimate_packages", "norm_materials", "norms" })
            await ExecPidAsync(conn, tx, $"DELETE FROM qs.{t} WHERE project_id = @p", pid.Value, ct);
        await ExecPidAsync(conn, tx, "UPDATE qs.projects SET active_estimate_version_id = NULL WHERE id = @p", pid.Value, ct);
        foreach (var t in new[] { "estimate_versions", "reporting_periods", "project_memberships" })
            await ExecPidAsync(conn, tx, $"DELETE FROM qs.{t} WHERE project_id = @p", pid.Value, ct);
        await ExecPidAsync(conn, tx, "DELETE FROM qs.projects WHERE id = @p", pid.Value, ct);

        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>All memberships (user + role) for a slug — snapshot before a re-import so the importer's
    /// purge (which recreates only the owner) doesn't silently drop editors/viewers/service users.</summary>
    public async Task<IReadOnlyList<(long UserId, string Role)>> GetMembershipsAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "SET ROLE qs_bypass", ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT m.user_id, m.role FROM qs.project_memberships m " +
            "JOIN qs.projects p ON p.id = m.project_id WHERE p.slug = @s", conn);
        cmd.Parameters.Add(Txt("@s", slug));
        var list = new List<(long, string)>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add((rd.GetInt64(0), rd.GetString(1)));
        return list;
    }

    /// <summary>Re-add a snapshotted membership set to the (possibly re-created) project of this slug.
    /// Idempotent: the importer already recreated the owner, so ON CONFLICT skips duplicates; on a failed
    /// import the transaction rolled back and the originals still exist, making this a safe no-op.</summary>
    public async Task RestoreMembershipsAsync(string slug, IReadOnlyList<(long UserId, string Role)> members, CancellationToken ct = default)
    {
        if (members.Count == 0) return;
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await ExecAsync(conn, "SET ROLE qs_bypass", ct);
        long? pid;
        await using (var cmd = new NpgsqlCommand("SELECT id FROM qs.projects WHERE slug = @s", conn))
        {
            cmd.Parameters.Add(Txt("@s", slug));
            pid = await cmd.ExecuteScalarAsync(ct) as long?;
        }
        if (pid is null) return;
        foreach (var (userId, role) in members)
        {
            await using var ins = new NpgsqlCommand(
                "INSERT INTO qs.project_memberships (project_id, user_id, role) VALUES (@p, @u, @r) " +
                "ON CONFLICT ON CONSTRAINT uq_membership DO NOTHING", conn);
            ins.Parameters.Add(Big("@p", pid.Value));
            ins.Parameters.Add(Big("@u", userId));
            ins.Parameters.Add(Txt("@r", role));
            await ins.ExecuteNonQueryAsync(ct);
        }
    }

    // ── helpers ──
    private static NpgsqlParameter Txt(string n, string v) => new(n, NpgsqlDbType.Text) { Value = v };
    private static NpgsqlParameter Big(string n, long v) => new(n, NpgsqlDbType.Bigint) { Value = v };

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ExecPidAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, long pid, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(Big("@p", pid));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
