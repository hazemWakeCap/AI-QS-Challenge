using Npgsql;
using NpgsqlTypes;

namespace QsEarlyWarning.Infrastructure.Postgres;

public sealed record PeriodInfo(long Id, int PeriodId, DateTime PeriodStart, string Status,
                                DateTimeOffset? OpenedAt, DateTimeOffset? ClosedAt);

/// <summary>Raised when a tenant procedure rejects a write (e.g. incomplete period close, closed-period
/// capture). Carries the database's typed message so the API can surface it.</summary>
public sealed class TenantWriteException : Exception
{
    public TenantWriteException(string message) : base(message) { }
}

/// <summary>
/// Runs the authorized-write workflow (period open/close, progress capture, estimate publish) as the
/// RLS-governed app role, with both transaction-local identities set. The SECURITY DEFINER procedures
/// re-check membership, so this is a real tenant boundary, not a trusted caller.
/// </summary>
public sealed class TenantWriteService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _appRole;

    public TenantWriteService(string connectionString, string appRole = "qs_app")
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _appRole = appRole;
    }

    public async Task<IReadOnlyList<PeriodInfo>> ListPeriodsAsync(long projectId, long userId, CancellationToken ct = default)
        => await InTenantTx(projectId, userId, async (conn, tx) =>
        {
            var rows = new List<PeriodInfo>();
            await using (var cmd = new NpgsqlCommand(
                "SELECT id, period_id, period_start, status, opened_at, closed_at FROM qs.reporting_periods " +
                "WHERE project_id = @p ORDER BY period_id", conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    rows.Add(new PeriodInfo(rd.GetInt64(0), rd.GetInt32(1), rd.GetDateTime(2), rd.GetString(3),
                        rd.IsDBNull(4) ? null : rd.GetFieldValue<DateTimeOffset>(4),
                        rd.IsDBNull(5) ? null : rd.GetFieldValue<DateTimeOffset>(5)));
            }
            return (IReadOnlyList<PeriodInfo>)rows;
        }, commit: false, ct);

    public Task OpenPeriodAsync(long projectId, long userId, int periodOrdinal, CancellationToken ct = default)
        => CallProcByOrdinal(projectId, userId, periodOrdinal, "CALL qs.sp_open_period(@p, @rp)", ct);

    public Task ClosePeriodAsync(long projectId, long userId, int periodOrdinal, CancellationToken ct = default)
        => CallProcByOrdinal(projectId, userId, periodOrdinal, "CALL qs.sp_close_period(@p, @rp)", ct);

    /// <summary>Monthly progress capture: set a cost centre's actual % complete for a period (open only).</summary>
    public Task<int> CaptureProgressAsync(long projectId, long userId, string bccId, int periodOrdinal, decimal actualPct, CancellationToken ct = default)
        => InTenantTx(projectId, userId, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE qs.cost_centre_periods SET actual_pct_complete = @pct " +
                "WHERE project_id = @p " +
                "  AND cost_centre_id = (SELECT id FROM qs.cost_centres WHERE project_id = @p AND bcc_id = @bcc) " +
                "  AND reporting_period_id = (SELECT id FROM qs.reporting_periods WHERE project_id = @p AND period_id = @ord)", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            cmd.Parameters.Add(new NpgsqlParameter("bcc", NpgsqlDbType.Text) { Value = bccId });
            cmd.Parameters.Add(new NpgsqlParameter("ord", NpgsqlDbType.Integer) { Value = periodOrdinal });
            cmd.Parameters.Add(new NpgsqlParameter("pct", NpgsqlDbType.Numeric) { Value = actualPct });
            return await cmd.ExecuteNonQueryAsync(ct);
        }, commit: true, ct);

    /// <summary>Post one cost delta to the append-only ledger (idempotent, open-period only).</summary>
    public Task PostCostDeltaAsync(long projectId, long userId, string bccId, int periodOrdinal,
        string rtype, decimal amount, string direction, string idempotencyKey, CancellationToken ct = default)
        => InTenantTx(projectId, userId, async (conn, tx) =>
        {
            // CALL cannot take subqueries as arguments — resolve the ids first.
            long ccId, rpId;
            await using (var look = new NpgsqlCommand(
                "SELECT (SELECT id FROM qs.cost_centres WHERE project_id=@p AND bcc_id=@bcc), " +
                "       (SELECT id FROM qs.reporting_periods WHERE project_id=@p AND period_id=@ord)", conn, tx))
            {
                look.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
                look.Parameters.Add(new NpgsqlParameter("bcc", NpgsqlDbType.Text) { Value = bccId });
                look.Parameters.Add(new NpgsqlParameter("ord", NpgsqlDbType.Integer) { Value = periodOrdinal });
                await using var rd = await look.ExecuteReaderAsync(ct);
                await rd.ReadAsync(ct);
                if (rd.IsDBNull(0)) throw new TenantWriteException($"cost centre {bccId} not found");
                if (rd.IsDBNull(1)) throw new TenantWriteException($"reporting period {periodOrdinal} not found");
                ccId = rd.GetInt64(0); rpId = rd.GetInt64(1);
            }
            await using var cmd = new NpgsqlCommand("CALL qs.sp_post_cost_delta(@p, @cc, @rp, @rt, @amt, @dir, @key)", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            cmd.Parameters.Add(new NpgsqlParameter("cc", NpgsqlDbType.Bigint) { Value = ccId });
            cmd.Parameters.Add(new NpgsqlParameter("rp", NpgsqlDbType.Bigint) { Value = rpId });
            cmd.Parameters.Add(new NpgsqlParameter("rt", NpgsqlDbType.Text) { Value = rtype });
            cmd.Parameters.Add(new NpgsqlParameter("amt", NpgsqlDbType.Numeric) { Value = amount });
            cmd.Parameters.Add(new NpgsqlParameter("dir", NpgsqlDbType.Text) { Value = direction });
            cmd.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = idempotencyKey });
            await cmd.ExecuteNonQueryAsync(ct);
            return 0;
        }, commit: true, ct);

    public Task RebaselinePeriodAsync(long projectId, long userId, int periodOrdinal, CancellationToken ct = default)
        => CallProcByOrdinal(projectId, userId, periodOrdinal, "CALL qs.sp_rebaseline_period(@p, @rp)", ct);

    /// <summary>One-time cutover of the project to the append-only ledger. Runs as qs_worker (the role
    /// granted EXECUTE on the cutover procedure); the procedure still re-checks the caller's membership.</summary>
    public Task CutoverToLedgerAsync(long projectId, long userId, CancellationToken ct = default)
        => InTenantTx(projectId, userId, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand("CALL qs.sp_cutover_to_ledger(@p)", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            await cmd.ExecuteNonQueryAsync(ct);
            return 0;
        }, commit: true, ct, role: "qs_worker");

    public Task PublishVersionAsync(long projectId, long userId, long versionId, CancellationToken ct = default)
        => InTenantTx(projectId, userId, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand("CALL qs.sp_publish_estimate_version(@p, @v)", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            cmd.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Bigint) { Value = versionId });
            await cmd.ExecuteNonQueryAsync(ct);
            return 0;
        }, commit: true, ct);

    // ── helpers ──
    private Task CallProcByOrdinal(long projectId, long userId, int periodOrdinal, string call, CancellationToken ct)
        => InTenantTx(projectId, userId, async (conn, tx) =>
        {
            long rpId;
            await using (var lookup = new NpgsqlCommand(
                "SELECT id FROM qs.reporting_periods WHERE project_id = @p AND period_id = @ord", conn, tx))
            {
                lookup.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
                lookup.Parameters.Add(new NpgsqlParameter("ord", NpgsqlDbType.Integer) { Value = periodOrdinal });
                if (await lookup.ExecuteScalarAsync(ct) is not long id)
                    throw new TenantWriteException($"reporting period {periodOrdinal} not found");
                rpId = id;
            }
            await using var cmd = new NpgsqlCommand(call, conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            cmd.Parameters.Add(new NpgsqlParameter("rp", NpgsqlDbType.Bigint) { Value = rpId });
            await cmd.ExecuteNonQueryAsync(ct);
            return 0;
        }, commit: true, ct);

    private async Task<T> InTenantTx<T>(long projectId, long userId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body, bool commit, CancellationToken ct, string? role = null)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, $"SET LOCAL ROLE {role ?? _appRole}", ct);
        await Exec(conn, tx, "SELECT set_config('app.current_user_id', @u, true)", ct, new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = userId.ToString() });
        await Exec(conn, tx, "SELECT set_config('app.current_project_id', @p, true)", ct, new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = projectId.ToString() });
        try
        {
            var result = await body(conn, tx);
            if (commit) await tx.CommitAsync(ct);
            return result;
        }
        catch (PostgresException ex)
        {
            throw new TenantWriteException(ex.MessageText);
        }
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(ps);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
