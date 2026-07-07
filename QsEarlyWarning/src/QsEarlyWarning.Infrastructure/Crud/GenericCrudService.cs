using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using QsEarlyWarning.Infrastructure.Postgres;

namespace QsEarlyWarning.Infrastructure.Crud;

/// <summary>
/// Governed generic CRUD over the tenant tables, run as the RLS-governed <c>qs_app</c> role with both
/// transaction-local identities set (same pattern as <see cref="TenantWriteService"/>). Column names
/// come only from <see cref="EntityRegistry"/> (a whitelist) and values are parameterised, so there is
/// no injection surface. Every invariant (RLS scoping, draft-only estimate edits, closed-period freeze,
/// currency immutability, FK RESTRICT, generated columns) is enforced by the database; this service just
/// surfaces the typed rejection as <see cref="TenantWriteException"/> (→ HTTP 409).
/// </summary>
public sealed class GenericCrudService
{
    private readonly NpgsqlDataSource _dataSource;
    private const string AppRole = "qs_app";

    public GenericCrudService(string connectionString) => _dataSource = NpgsqlDataSource.Create(connectionString);

    public Task<List<Dictionary<string, object?>>> ListAsync(EntityDescriptor e, long projectId, long userId,
        IReadOnlyDictionary<string, string>? filters, CancellationToken ct = default)
        => InTenantTx(projectId, userId, async (conn, tx) =>
        {
            var where = new List<string> { "project_id = @p" };
            var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            if (filters is not null)
            {
                var i = 0;
                foreach (var (k, v) in filters)
                {
                    var col = e.Columns.FirstOrDefault(c => c.Name == k);
                    if (col is null) continue;                       // whitelist: only real columns
                    var pn = $"f{i++}";
                    where.Add($"{k} = @{pn}");
                    cmd.Parameters.Add(Param(pn, col.Kind, v));
                }
            }
            cmd.CommandText = $"SELECT * FROM {e.Table} WHERE {string.Join(" AND ", where)} ORDER BY id";
            await using (cmd)
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
                return await ReadAll(rd, ct);
        }, commit: false, ct);

    public async Task<Dictionary<string, object?>?> GetAsync(EntityDescriptor e, long id, long projectId, long userId, CancellationToken ct = default)
    {
        var rows = await InTenantTx(projectId, userId, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand($"SELECT * FROM {e.Table} WHERE project_id = @p AND id = @id", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            return await ReadAll(rd, ct);
        }, commit: false, ct);
        return rows.FirstOrDefault();
    }

    public Task<long> CreateAsync(EntityDescriptor e, Dictionary<string, JsonElement> body, long projectId, long userId, CancellationToken ct = default)
        => InTenantTx(projectId, userId, async (conn, tx) =>
        {
            var cols = new List<string> { "project_id" };
            var vals = new List<string> { "@p" };
            var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            foreach (var c in e.Columns.Where(c => c.Insertable && body.ContainsKey(c.Name)))
            {
                cols.Add(c.Name); vals.Add($"@{c.Name}");
                cmd.Parameters.Add(ParamJson(c.Name, c.Kind, body[c.Name]));
            }
            cmd.CommandText = $"INSERT INTO {e.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)}) RETURNING id";
            await using (cmd) return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }, commit: true, ct);

    public Task<bool> UpdateAsync(EntityDescriptor e, long id, Dictionary<string, JsonElement> body, long projectId, long userId, CancellationToken ct = default)
        => InTenantTx(projectId, userId, async (conn, tx) =>
        {
            var sets = new List<string>();
            var cmd = new NpgsqlCommand { Connection = conn, Transaction = tx };
            foreach (var c in e.Columns.Where(c => c.Updatable && body.ContainsKey(c.Name)))
            {
                sets.Add($"{c.Name} = @{c.Name}");
                cmd.Parameters.Add(ParamJson(c.Name, c.Kind, body[c.Name]));
            }
            if (sets.Count == 0) return false;
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
            cmd.CommandText = $"UPDATE {e.Table} SET {string.Join(", ", sets)} WHERE project_id = @p AND id = @id";
            await using (cmd) return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }, commit: true, ct);

    public Task<bool> DeleteAsync(EntityDescriptor e, long id, long projectId, long userId, CancellationToken ct = default)
        => InTenantTx(projectId, userId, async (conn, tx) =>
        {
            await using var cmd = new NpgsqlCommand($"DELETE FROM {e.Table} WHERE project_id = @p AND id = @id", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Bigint) { Value = projectId });
            cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }, commit: true, ct);

    // ── helpers ──
    private static async Task<List<Dictionary<string, object?>>> ReadAll(NpgsqlDataReader rd, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        while (await rd.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(rd.FieldCount);
            for (var i = 0; i < rd.FieldCount; i++)
                row[rd.GetName(i)] = await rd.IsDBNullAsync(i, ct) ? null : rd.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }

    private static NpgsqlParameter Param(string name, ColKind kind, string raw)
    {
        var (t, v) = kind switch
        {
            ColKind.Int => (NpgsqlDbType.Integer, (object)int.Parse(raw, CultureInfo.InvariantCulture)),
            ColKind.Bigint => (NpgsqlDbType.Bigint, long.Parse(raw, CultureInfo.InvariantCulture)),
            ColKind.Numeric => (NpgsqlDbType.Numeric, decimal.Parse(raw, CultureInfo.InvariantCulture)),
            ColKind.Bool => (NpgsqlDbType.Boolean, bool.Parse(raw)),
            ColKind.Date => (NpgsqlDbType.Date, DateTime.Parse(raw, CultureInfo.InvariantCulture)),
            _ => (NpgsqlDbType.Text, raw),
        };
        return new NpgsqlParameter(name, t) { Value = v };
    }

    private static NpgsqlParameter ParamJson(string name, ColKind kind, JsonElement v)
    {
        var t = kind switch
        {
            ColKind.Int => NpgsqlDbType.Integer, ColKind.Bigint => NpgsqlDbType.Bigint,
            ColKind.Numeric => NpgsqlDbType.Numeric, ColKind.Bool => NpgsqlDbType.Boolean,
            ColKind.Date => NpgsqlDbType.Date, _ => NpgsqlDbType.Text,
        };
        object value = v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => DBNull.Value,
            _ => kind switch
            {
                ColKind.Int => v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.Parse(v.GetString()!, CultureInfo.InvariantCulture),
                ColKind.Bigint => v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.Parse(v.GetString()!, CultureInfo.InvariantCulture),
                ColKind.Numeric => v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : decimal.Parse(v.GetString()!, CultureInfo.InvariantCulture),
                ColKind.Bool => v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : bool.Parse(v.GetString()!),
                ColKind.Date => DateTime.Parse(v.GetString()!, CultureInfo.InvariantCulture),
                _ => (object?)v.GetString() ?? DBNull.Value,
            },
        };
        // empty string on a non-text column → treat as NULL
        if (value is string s && s.Length == 0 && kind != ColKind.Text) value = DBNull.Value;
        return new NpgsqlParameter(name, t) { Value = value };
    }

    private async Task<T> InTenantTx<T>(long projectId, long userId, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body, bool commit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await Exec(conn, tx, $"SET LOCAL ROLE {AppRole}", ct);
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
