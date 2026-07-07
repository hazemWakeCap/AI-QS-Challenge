using Npgsql;
using NpgsqlTypes;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Infrastructure.Postgres;

/// <summary>
/// Reads a project's panel from <c>qs.cost_centre_evm</c> as the app role, under RLS (plan §5b).
///
/// Every read opens a transaction that assumes the app role and sets BOTH transaction-local
/// identities (app.current_user_id, app.current_project_id) via set_config(..., is_local => true)
/// before touching any tenant table. The security_invoker EVM view then enforces the caller's RLS,
/// so a non-member or a mismatched project yields an empty panel rather than a leak. The result is
/// a fully-materialized, detached list — no open reader escapes the connection.
///
/// Uses a shared <see cref="NpgsqlDataSource"/> (the async, pooled equivalent of an
/// <c>IDbContextFactory</c>): the loader itself holds no per-request state and is safe to share.
/// </summary>
public sealed class PostgresPanelLoader : IProjectPanelSource, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _appRole;

    /// <param name="connectionString">Login role may be the app role directly, or a superuser that
    /// SET ROLEs down to <paramref name="appRole"/> (used in local/dev where one login exists).</param>
    /// <param name="appRole">The RLS-governed role to assume for reads (default qs_app).</param>
    public PostgresPanelLoader(string connectionString, string appRole = "qs_app")
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _appRole = appRole;
    }

    public async Task<IReadOnlyList<CostCentrePeriod>> LoadAsync(long projectId, long userId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Assume the RLS-governed role and set transaction-local identity BEFORE any tenant read.
        await Exec(conn, tx, $"SET LOCAL ROLE {_appRole}", ct);
        await Exec(conn, tx, "SELECT set_config('app.current_user_id', @u, true)", ct,
            new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = userId.ToString() });
        await Exec(conn, tx, "SELECT set_config('app.current_project_id', @p, true)", ct,
            new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = projectId.ToString() });

        const string sql = """
            SELECT bcc_id, period_id, discipline, package_code, wbs_code, alert_level,
                   bac_amount, planned_pct, pv_amount, actual_pct_complete, ev_amount, earned_qty,
                   ac_total_amount, cpi, spi, cv_amount, eac_amount, vac_amount, pct_budget_consumed,
                   ac_material_amount, ac_manpower_amount, ac_equipment_amount, ac_subcontract_amount
            FROM qs.cost_centre_evm
            WHERE project_id = @proj
            ORDER BY bcc_id, period_id
            """;

        var rows = new List<CostCentrePeriod>();
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter("proj", NpgsqlDbType.Bigint) { Value = projectId });
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                rows.Add(new CostCentrePeriod
                {
                    BccId = rd.GetString(0),
                    PeriodId = rd.GetInt32(1),
                    Discipline = Str(rd, 2),
                    PackageCode = rd.GetString(3),
                    WbsCode = Str(rd, 4),
                    AlertLevel = DenormAlert(Str(rd, 5)),
                    BacAed = Dbl(rd, 6),
                    PlanPctComplete = Dbl(rd, 7),
                    PvAed = Dbl(rd, 8),
                    ActualPctComplete = Dbl(rd, 9),
                    EvAed = Dbl(rd, 10),
                    EarnedQtyCumul = Dbl(rd, 11),
                    AcCumulative = Dbl(rd, 12),
                    Cpi = Dbl(rd, 13),
                    Spi = Dbl(rd, 14),
                    CvAed = Dbl(rd, 15),
                    EacAed = Dbl(rd, 16),
                    VacAed = Dbl(rd, 17),
                    PctBudgetConsumed = Dbl(rd, 18),
                    AcMaterial = Dbl(rd, 19),
                    AcManpower = Dbl(rd, 20),
                    AcEquipment = Dbl(rd, 21),
                    AcSubcontract = Dbl(rd, 22),
                    // Rolling3mCpi / VariancePct / EacVsBacRatio are recorded-only signals not used by
                    // the scorer; left null (the ranking depends solely on gap + cpi).
                });
            }
        }

        await tx.RollbackAsync(ct);   // read-only
        return rows;
    }

    public async Task<bool> IsAuthorizedAsync(long projectId, long userId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, $"SET LOCAL ROLE {_appRole}", ct);
        await Exec(conn, tx, "SELECT set_config('app.current_user_id', @u, true)", ct,
            new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = userId.ToString() });
        await Exec(conn, tx, "SELECT set_config('app.current_project_id', @p, true)", ct,
            new NpgsqlParameter("p", NpgsqlDbType.Text) { Value = projectId.ToString() });

        // RLS on qs.projects requires id = current_project AND membership — so this returns true only
        // for a genuine member of the selected project. Same policy path as every data read.
        await using var cmd = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM qs.projects)", conn, tx);
        var authorized = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.RollbackAsync(ct);
        return authorized;
    }

    // The view emits NOT_STARTED; the domain/Excel convention is "NOT STARTED".
    private static string? DenormAlert(string? a) => a == "NOT_STARTED" ? "NOT STARTED" : a;

    private static string? Str(NpgsqlDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetString(i);
    private static double? Dbl(NpgsqlDataReader rd, int i) => rd.IsDBNull(i) ? null : (double)rd.GetDecimal(i);

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params NpgsqlParameter[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(ps);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
