using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Infrastructure.Postgres;

/// <summary>
/// Project-aware, async panel source (plan §3 codex correction, §5b). Unlike the Excel
/// <c>IPanelLoader</c> (single file, synchronous, singleton-unsafe), this reads one project's panel
/// from Postgres under the RLS boundary and returns a detached, immutable snapshot.
/// </summary>
public interface IProjectPanelSource
{
    /// <summary>
    /// Reads the computed EVM panel for <paramref name="projectId"/> as the authenticated
    /// <paramref name="userId"/>. Both identities are set transaction-locally and enforced by RLS,
    /// so a non-member (or a mismatched project) sees nothing.
    /// </summary>
    Task<IReadOnlyList<CostCentrePeriod>> LoadAsync(long projectId, long userId, CancellationToken ct = default);

    /// <summary>
    /// RLS-true authorization probe: is <paramref name="userId"/> a member of <paramref name="projectId"/>?
    /// Must be checked per request — the snapshot cache holds project data built once, so authorization
    /// cannot be inferred from a cache hit.
    /// </summary>
    Task<bool> IsAuthorizedAsync(long projectId, long userId, CancellationToken ct = default);
}
