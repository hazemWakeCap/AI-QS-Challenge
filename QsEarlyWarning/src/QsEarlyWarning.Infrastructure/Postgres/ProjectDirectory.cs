using Npgsql;
using NpgsqlTypes;

namespace QsEarlyWarning.Infrastructure.Postgres;

/// <summary>One project a user can see in the tenant switcher.</summary>
public sealed record ProjectInfo(
    long Id, string Slug, string Name, string ReportingCurrency,
    long? ActiveEstimateVersionId, bool LedgerActive);

/// <summary>Lists the projects a user belongs to (the multi-tenant switcher's data). Runs as the app
/// role with only the user id set — the member-wide SELECT policy on qs.projects (0009) scopes the
/// result to the caller's memberships.</summary>
public sealed class ProjectDirectory
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _appRole;

    public ProjectDirectory(string connectionString, string appRole = "qs_app")
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _appRole = appRole;
    }

    public async Task<IReadOnlyList<ProjectInfo>> ListForUserAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var role = new NpgsqlCommand($"SET LOCAL ROLE {_appRole}", conn, tx)) await role.ExecuteNonQueryAsync(ct);
        await using (var setUser = new NpgsqlCommand("SELECT set_config('app.current_user_id', @u, true)", conn, tx))
        {
            setUser.Parameters.Add(new NpgsqlParameter("u", NpgsqlDbType.Text) { Value = userId.ToString() });
            await setUser.ExecuteNonQueryAsync(ct);
        }

        var rows = new List<ProjectInfo>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT id, slug, name, reporting_currency, active_estimate_version_id, ledger_active " +
            "FROM qs.projects ORDER BY slug", conn, tx))
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                rows.Add(new ProjectInfo(
                    rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                    rd.IsDBNull(4) ? null : rd.GetInt64(4), rd.GetBoolean(5)));
        }
        // read-only; the transaction rolls back on disposal (reader is closed above first)
        return rows;
    }
}
