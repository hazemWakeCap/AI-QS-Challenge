using System.Collections.Concurrent;
using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Forecasting;
using QsEarlyWarning.Core.StressTest;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Domain.Estimate;
using QsEarlyWarning.Infrastructure.Postgres;

namespace QsEarlyWarning.Core.Registry;

/// <summary>
/// A detached, immutable per-project snapshot: the materialized panel, the trained model, and the
/// reporting origins derived from the data (plan §5b). Replaces <c>ModelProvider</c>'s single global
/// <c>ModelSnapshot</c>.
/// </summary>
public sealed record ProjectSnapshot
{
    public required long ProjectId { get; init; }
    public required long BuiltForUserId { get; init; }
    public required IReadOnlyList<CostCentrePeriod> Panel { get; init; }
    public required TrainedModel Model { get; init; }
    public required int MinPeriod { get; init; }
    /// <summary>Latest reporting period present — the forecast origin, derived from the DB (not a constant 12).</summary>
    public required int ForecastPeriod { get; init; }
    public required DateTimeOffset BuiltAtUtc { get; init; }
    /// <summary>Idea-2 incremental-spend forecaster + its back-test (null if it could not be fit). Built at
    /// snapshot Build() and refreshed via RebuildAsync — the registry has no change detection.</summary>
    public IncrementalSpendForecaster? Forecaster { get; init; }
    public ForecastValidationSummary? ForecastBacktest { get; init; }
    /// <summary>Idea-3 Estimate Assumption Stress Test (null unless this is the estimate's owning project,
    /// or if the workbook is unavailable). Built at snapshot Build() from the workbook + this panel.</summary>
    public StressTestReport? StressTest { get; init; }

    public int RowCount => Panel.Count;
    public int CentreCount => Panel.Select(p => p.BccId).Distinct(StringComparer.Ordinal).Count();
}

public interface IProjectSnapshotRegistry
{
    /// <summary>Cached snapshot for the project, building it once on first use.</summary>
    Task<ProjectSnapshot> GetOrBuildAsync(long projectId, long userId, CancellationToken ct = default);
    /// <summary>Force a rebuild; on failure the previous snapshot is retained (last-known-good) and the error rethrown.</summary>
    Task<ProjectSnapshot> RebuildAsync(long projectId, long userId, CancellationToken ct = default);
    ProjectSnapshot? TryGet(long projectId);
}

/// <summary>
/// Project-keyed, async, thread-safe snapshot registry (plan §3 codex correction, §5b). Reads through
/// the RLS-scoped <see cref="IProjectPanelSource"/>, materializes a detached snapshot, trains the model
/// off the read, caches by immutable project id, de-duplicates concurrent rebuilds per project, and
/// keeps the last-known-good snapshot if a rebuild fails.
///
/// Origins are derived from the DB panel (forecast = latest present period). NOTE: the current
/// <see cref="RollingOriginEvaluator"/> still uses the frozen Tower-X period constants; wiring the
/// derived origins through the evaluator is the remaining dynamic-origin step (tracked for Phase 2b).
/// </summary>
public sealed class ProjectSnapshotRegistry : IProjectSnapshotRegistry
{
    private readonly IProjectPanelSource _source;
    private readonly IEstimateSource? _estimate;
    private readonly ConcurrentDictionary<long, ProjectSnapshot> _cache = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public ProjectSnapshotRegistry(IProjectPanelSource source, IEstimateSource? estimate = null)
    {
        _source = source;
        _estimate = estimate;
    }

    public ProjectSnapshot? TryGet(long projectId) => _cache.TryGetValue(projectId, out var s) ? s : null;

    public async Task<ProjectSnapshot> GetOrBuildAsync(long projectId, long userId, CancellationToken ct = default)
        => _cache.TryGetValue(projectId, out var s) ? s : await BuildLocked(projectId, userId, forceRebuild: false, ct);

    public Task<ProjectSnapshot> RebuildAsync(long projectId, long userId, CancellationToken ct = default)
        => BuildLocked(projectId, userId, forceRebuild: true, ct);

    private async Task<ProjectSnapshot> BuildLocked(long projectId, long userId, bool forceRebuild, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(projectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another caller may have built it while we waited (concurrent-rebuild de-duplication).
            if (!forceRebuild && _cache.TryGetValue(projectId, out var existing))
                return existing;

            ProjectSnapshot built;
            try
            {
                built = await Build(projectId, userId, ct);
            }
            catch when (_cache.TryGetValue(projectId, out _))
            {
                // last-known-good retained; surface the failure to the caller
                throw;
            }

            _cache[projectId] = built;
            return built;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ProjectSnapshot> Build(long projectId, long userId, CancellationToken ct)
    {
        var panel = await _source.LoadAsync(projectId, userId, ct);
        if (panel.Count == 0)
            throw new InvalidOperationException(
                $"Empty panel for project {projectId} (no rows visible to user {userId} — missing membership or unimported project).");

        var model = new RollingOriginEvaluator().Train(panel);
        var periods = panel.Select(p => p.PeriodId).ToList();

        // Idea-2 forecaster: fit the serving model + run the back-test. Degrade gracefully — a
        // forecaster failure must not sink the whole snapshot (watchlist/EVM still work).
        IncrementalSpendForecaster? forecaster = null;
        ForecastValidationSummary? backtest = null;
        try
        {
            var f = new IncrementalSpendForecaster();
            f.Fit(panel, model.Origins);
            backtest = new ForecastEvaluator().Evaluate(panel, model.Origins);
            forecaster = f;
        }
        catch { /* forecaster unavailable for this project; leave null */ }

        // Idea-3 stress test: only the estimate's owning project returns a model (gated by project id);
        // Class 1+2 use the workbook, Class 3 uses this panel. Degrade gracefully like the forecaster.
        StressTestReport? stressTest = null;
        try
        {
            var estimate = _estimate?.TryLoadForProject(projectId);
            if (estimate is not null)
                stressTest = new EstimateStressTester().Run(estimate, panel);
        }
        catch { /* stress test unavailable for this project; leave null */ }

        return new ProjectSnapshot
        {
            ProjectId = projectId,
            BuiltForUserId = userId,
            Panel = panel,
            Model = model,
            MinPeriod = periods.Min(),
            ForecastPeriod = periods.Max(),
            BuiltAtUtc = DateTimeOffset.UtcNow,
            Forecaster = forecaster,
            ForecastBacktest = backtest,
            StressTest = stressTest,
        };
    }
}
