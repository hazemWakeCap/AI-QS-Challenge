using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

[ApiController]
[Route("api/v1/watchlist/backtest")]
public sealed class WatchlistBacktestController : ControllerBase
{
    private readonly IProjectSnapshotRegistry _registry;
    private readonly IProjectPanelSource _panels;
    private readonly ProjectResolver _resolver;
    private readonly WatchlistBacktestService _backtest;
    private readonly TenantContext _ctx;

    public WatchlistBacktestController(IProjectSnapshotRegistry registry, IProjectPanelSource panels,
                                       ProjectResolver resolver, WatchlistBacktestService backtest, TenantContext ctx)
    {
        _registry = registry;
        _panels = panels;
        _resolver = resolver;
        _backtest = backtest;
        _ctx = ctx;
    }

    /// <summary>
    /// Grades the watchlist against reality for a past origin period: the top-k flagged centres, each
    /// with its ACTUAL next-period Alert_Level and a hit/miss verdict, plus the model-level headline
    /// (rule vs best CPI-native baseline). Same RLS authorization as the watchlist. Only labeled
    /// origins are backtestable — the live forecast period has no successor, so it returns 400.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<BacktestResponseDto>> Get(
        [FromQuery] int period, [FromQuery] int k = 5, CancellationToken ct = default)
    {
        if (!_ctx.IsAuthenticated)
            return Unauthorized("Provide X-User-Id and X-Project-Slug (authenticated identity + selected project).");
        if (k is not (5 or 10))
            return BadRequest("k must be 5 or 10.");

        var projectId = await _resolver.ResolveAsync(_ctx.ProjectSlug!, ct);
        if (projectId is null)
            return NotFound($"Unknown project '{_ctx.ProjectSlug}'.");

        if (!await _panels.IsAuthorizedAsync(projectId.Value, _ctx.UserId!.Value, ct))
            return StatusCode(StatusCodes.Status403Forbidden,
                $"User {_ctx.UserId} is not a member of project '{_ctx.ProjectSlug}'.");

        ProjectSnapshot snapshot;
        try
        {
            snapshot = await _registry.GetOrBuildAsync(projectId.Value, _ctx.UserId!.Value, ct);
        }
        catch (InvalidOperationException)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                $"No data visible for project '{_ctx.ProjectSlug}' as user {_ctx.UserId} (membership or import missing).");
        }

        var origins = snapshot.Model.Origins;
        if (!WatchlistBacktestService.IsBacktestable(origins, period))
            return BadRequest($"period must be a labeled origin in [{origins.FirstOrigin}, {origins.LastLabeledPeriod}] " +
                              $"to grade against the actual next period (period {origins.ForecastPeriod} is the live " +
                              "forecast — no next period exists yet to check it against).");

        var result = _backtest.Evaluate(snapshot.Panel, period, k, snapshot.Model);
        if (result is null)
            return BadRequest($"No model artifact serves period {period}.");

        // Model-level headline for the "beats the honest baselines" bar, from the frozen summary.
        var summary = snapshot.Model.Summary;
        var rule = summary.Rule.FirstOrDefault(r => r.K == EvmThresholds.SelectionK);
        var bestBaseline = summary.CpiNative
            .Where(r => r.K == EvmThresholds.SelectionK)
            .OrderByDescending(r => r.MacroPrecision ?? 0)
            .FirstOrDefault();

        var rows = result.Rows
            .Select((br, i) => new BacktestRowDto(
                i + 1, br.Row.BccId, br.Row.Discipline, br.Row.PackageCode,
                br.Row.RiskScore, br.Row.Cpi, br.Row.Gap, br.Row.RiskIndicators,
                br.ActualNextAlert, br.Hit))
            .ToList();

        return Ok(new BacktestResponseDto(
            result.Period, result.NextPeriod, result.K, result.TrainingCutoffPeriod,
            result.Eligible, result.Positives, result.Hits, result.PrecisionAtK, rows,
            origins.FirstOrigin, origins.LastLabeledPeriod,
            rule?.MacroPrecision, bestBaseline?.MacroPrecision,
            bestBaseline?.ScorerLabel, summary.TotalTransitions,
            "Hindsight backtest: the leakage-safe out-of-fold model scored this period, graded against " +
            "the actual next period in the project's own history. Exploratory single-project evidence."));
    }
}
