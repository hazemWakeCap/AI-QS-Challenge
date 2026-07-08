using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;

namespace QsEarlyWarning.Web.API.Tenancy;

/// <summary>Resolves a project slug to its surrogate id (cached). Runs as the bypass role — slug→id
/// is not tenant-sensitive, and RLS still gates every actual data read downstream.</summary>
public sealed class ProjectResolver
{
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, long> _cache = new();

    public ProjectResolver(string connectionString) => _connectionString = connectionString;

    public async Task<long?> ResolveAsync(string slug, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(slug, out var cached)) return cached;

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using (var role = new NpgsqlCommand("SET ROLE qs_bypass", conn)) await role.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id FROM qs.projects WHERE slug = @s", conn);
        cmd.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Text) { Value = slug });
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is long id) { _cache[slug] = id; return id; }
        return null;
    }

    /// <summary>Drop a slug from the cache. Call after a re-import (the importer purges + re-inserts the
    /// project row, so the id changes) or a delete/rename, so the next resolve reflects the new state.</summary>
    public void Invalidate(string slug) => _cache.TryRemove(slug, out _);
}
