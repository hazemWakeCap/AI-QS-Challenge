using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Forecasting;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

/// <summary>Idea-2 incremental-spend forecaster, served from the project snapshot. Live forecasts are
/// anchored at the latest origin only; historical origins are evaluated only in the back-test.</summary>
[ApiController]
[Route("api/v1/forecast")]
public sealed class ForecastController : ControllerBase
{
    private readonly IProjectSnapshotRegistry _registry;
    private readonly ProjectDirectory _directory;
    private readonly TenantContext _ctx;

    public ForecastController(IProjectSnapshotRegistry registry, ProjectDirectory directory, TenantContext ctx)
    {
        _registry = registry;
        _directory = directory;
        _ctx = ctx;
    }

    [HttpGet("cost-centres")]
    public async Task<IActionResult> CostCentres(CancellationToken ct)
    {
        var (snap, err) = await Resolve(ct); if (err is not null) return err;
        if (snap!.Forecaster is null) return NoForecast();
        var items = snap.Forecaster.AllCentres().Select(f =>
        {
            var h1 = f.Increments.FirstOrDefault(b => b.Horizon == 1);
            var disc = snap.Panel.FirstOrDefault(p => p.BccId == f.BccId)?.Discipline;
            return new ForecastListItemDto(f.BccId, disc, Round(f.ProgressPct), f.Trust.ToString(),
                Round(h1?.P50 ?? 0), San(h1?.P10), San(h1?.P90), h1?.Available ?? false);
        }).ToList();
        return Ok(items);
    }

    [HttpGet("cone")]
    public async Task<IActionResult> Cone([FromQuery] string bcc, CancellationToken ct)
    {
        var (snap, err) = await Resolve(ct); if (err is not null) return err;
        if (snap!.Forecaster is null) return NoForecast();
        var f = snap.Forecaster.ForecastCentre(bcc);
        if (f is null) return NotFound(new { error = $"no forecast for '{bcc}' at the latest origin." });
        return Ok(new CentreForecastDto(
            f.BccId, f.OriginPeriod, Round(f.ProgressPct), Round(f.Bac), Round(f.AcAtOrigin), f.Trust.ToString(),
            f.Increments.Select(b => new HorizonBandDto(b.Horizon, Round(b.P50), San(b.P10), San(b.P90), b.Available)).ToList(),
            f.CumulativeCone.Select(c => new ConePointDto(c.Period, Round(c.P50), San(c.P10), San(c.P90))).ToList(),
            f.CumulativeConeAvailable, San(f.DirectionalFinalCost)));
    }

    [HttpGet("rollup")]
    public async Task<IActionResult> Rollup(CancellationToken ct)
    {
        var (snap, err) = await Resolve(ct); if (err is not null) return err;
        if (snap!.Forecaster is null) return NoForecast();
        var r = snap.Forecaster.Rollup();
        return Ok(new ProjectSpendScenarioDto(r.OriginPeriod, Round(r.P10), Round(r.P50), Round(r.P90), r.Centres, r.Draws));
    }

    [HttpGet("backtest")]
    public async Task<IActionResult> Backtest(CancellationToken ct)
    {
        var (snap, err) = await Resolve(ct); if (err is not null) return err;
        if (snap!.ForecastBacktest is null) return NoForecast();
        var s = snap.ForecastBacktest;
        return Ok(new ForecastBacktestDto(s.Provenance, s.OriginMin, s.OriginMax, s.FoldsEvaluated, s.FoldsSkipped,
            s.Overall.Select(Map).ToList(), s.EarlyBand.Select(Map).ToList(), s.Notes));
    }

    private static HorizonMetricDto Map(HorizonMetric m) => new(
        m.Predictor, m.Horizon, m.N, Round(m.MaePctOfBac), Round(m.Wape),
        San(m.CoverageP10P90), San(m.CoverageLow), San(m.CoverageHigh), m.FallbackCount);

    private IActionResult NoForecast() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "forecast unavailable for this project (insufficient data to fit)." });

    private async Task<(ProjectSnapshot? Snap, IActionResult? Error)> Resolve(CancellationToken ct)
    {
        if (!_ctx.IsAuthenticated) return (null, Unauthorized("Provide X-User-Id and X-Project-Slug."));
        var mine = await _directory.ListForUserAsync(_ctx.UserId!.Value, ct);
        var project = mine.FirstOrDefault(p => p.Slug == _ctx.ProjectSlug);
        if (project is null) return (null, StatusCode(StatusCodes.Status403Forbidden, new { error = $"not a member of '{_ctx.ProjectSlug}'." }));
        try { return (await _registry.GetOrBuildAsync(project.Id, _ctx.UserId.Value, ct), null); }
        catch (InvalidOperationException) { return (null, StatusCode(StatusCodes.Status404NotFound, new { error = "no data for this project yet." })); }
    }

    private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 4) : 0;
    private static double? San(double? v) => v is double d && double.IsFinite(d) ? Math.Round(d, 4) : null;
}
