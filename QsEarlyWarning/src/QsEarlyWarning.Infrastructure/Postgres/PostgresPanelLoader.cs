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

        // zone_code is a cost-centre DIMENSION attribute, not an EVM figure, so it is joined from
        // qs.cost_centres rather than added to the computed-EVM view (0003 stays the single source
        // of truth for money). RLS applies to both relations under the GUCs set above.
        const string sql = """
            SELECT e.bcc_id, e.period_id, e.discipline, e.package_code, e.wbs_code, e.alert_level,
                   e.bac_amount, e.planned_pct, e.pv_amount, e.actual_pct_complete, e.ev_amount, e.earned_qty,
                   e.ac_total_amount, e.cpi, e.spi, e.cv_amount, e.eac_amount, e.vac_amount, e.pct_budget_consumed,
                   e.ac_material_amount, e.ac_manpower_amount, e.ac_equipment_amount, e.ac_subcontract_amount,
                   e.budget_qty, c.zone_code
            FROM qs.cost_centre_evm e
            JOIN qs.cost_centres    c ON c.project_id = e.project_id AND c.id = e.cost_centre_id
            WHERE e.project_id = @proj
            ORDER BY e.bcc_id, e.period_id
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
                    BudgetQty = Dbl(rd, 23),
                    ZoneArea = Str(rd, 24),
                    // Unit is not exposed by the EVM view; left null on the DB path (best-effort label only).
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

        // Authorize the SELECTED project specifically via the same membership predicate every data
        // policy uses. (Checking fn_is_member directly is robust even though projects now also has a
        // permissive member-wide SELECT policy for the tenant switcher.)
        await using var cmd = new NpgsqlCommand(
            "SELECT qs.fn_is_member(nullif(current_setting('app.current_project_id', true), '')::bigint)", conn, tx);
        var authorized = (await cmd.ExecuteScalarAsync(ct)) is true;
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
