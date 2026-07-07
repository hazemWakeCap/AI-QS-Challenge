using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

[ApiController]
[Route("api/v1/watchlist")]
public sealed class WatchlistController : ControllerBase
{
    private readonly IProjectSnapshotRegistry _registry;
    private readonly IProjectPanelSource _panels;
    private readonly ProjectResolver _resolver;
    private readonly WatchlistScoringService _scoring;
    private readonly TenantContext _ctx;

    public WatchlistController(IProjectSnapshotRegistry registry, IProjectPanelSource panels,
                               ProjectResolver resolver, WatchlistScoringService scoring, TenantContext ctx)
    {
        _registry = registry;
        _panels = panels;
        _resolver = resolver;
        _scoring = scoring;
        _ctx = ctx;
    }

    /// <summary>
    /// Ranked GREEN-centres-about-to-tip for a period, served from Postgres for the caller's selected
    /// project under RLS (plan §7 Phase 2). 401 = no identity; 403/404 = not a member / unknown
    /// project; 400 = malformed period/k; 404 = valid period with no matching artifact.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<WatchlistResponseDto>> Get(
        [FromQuery] int period, [FromQuery] int k = 10, CancellationToken ct = default)
    {
        if (!_ctx.IsAuthenticated)
            return Unauthorized("Provide X-User-Id and X-Project-Slug (authenticated identity + selected project).");
        if (k is not (5 or 10))
            return BadRequest("k must be 5 or 10.");

        var projectId = await _resolver.ResolveAsync(_ctx.ProjectSlug!, ct);
        if (projectId is null)
            return NotFound($"Unknown project '{_ctx.ProjectSlug}'.");

        // Authorize EVERY request against RLS — the snapshot cache holds project data built once, so a
        // cache hit must never stand in for the membership check (else a non-member could be served
        // another user's cached snapshot).
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
        if (period < origins.Periods[0] || period > origins.ForecastPeriod)
            return BadRequest($"period must be in [{origins.Periods[0]}, {origins.ForecastPeriod}] for this project.");

        var result = _scoring.ScorePeriod(snapshot.Panel, period, snapshot.Model);
        if (result.Status == ScoreStatus.NoArtifact)
            return NotFound($"No model artifact serves period {period} (retrospective range is " +
                            $"{origins.FirstOrigin}..{origins.LastLabeledPeriod}; forecast is {origins.ForecastPeriod}).");

        var rows = result.Rows
            .Take(k)
            .Select((r, i) => new WatchlistRowDto(
                i + 1, r.BccId, r.Discipline, r.PackageCode, r.RiskScore, r.Cpi, r.Gap, r.RiskIndicators))
            .ToList();

        return Ok(new WatchlistResponseDto(
            period, k, result.IsForecast, result.ArtifactVersion!, result.TrainingCutoffPeriod,
            result.Rows.Count, rows));
    }
}
