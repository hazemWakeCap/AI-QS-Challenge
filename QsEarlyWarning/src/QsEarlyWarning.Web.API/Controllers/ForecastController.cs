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

    /// <summary>
    /// Physical percent complete projected past the last reported period — what the 4D build sequence
    /// consumes to keep rising after the workbook stops.
    ///
    /// <paramref name="bcc"/> is a comma-separated centre list; omit it for every centre. <paramref name="through"/>
    /// is the last period to project; omit it to run to the period the slowest requested centre tops out.
    /// Both are capped at the forecaster's max horizon past the origin.
    /// </summary>
    [HttpGet("progress")]
    public async Task<IActionResult> Progress([FromQuery] string? bcc, [FromQuery] int? through, CancellationToken ct)
    {
        var (snap, err) = await Resolve(ct); if (err is not null) return err;
        if (snap!.ProgressForecaster is null) return NoForecast();

        var ids = bcc?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var f = snap.ProgressForecaster.Project(ids, through);

        return Ok(new ProgressForecastDto(
            f.OriginPeriod, f.HorizonPeriod, f.BacktestedThroughPeriod, f.SuggestedHorizonPeriod, f.Method,
            f.Centres.Select(c => new CentreProgressDto(
                c.BccId, c.OriginPeriod, Round(c.ActualPctAtOrigin), Round(c.PacePctPerPeriod),
                c.ProjectedFinishPeriod, c.Stalled, c.AlertAtOrigin,
                c.Points.Select(p => new ProgressPointDto(
                    p.Period, Round(p.P50Pct), San(p.P10Pct), San(p.P90Pct), p.Tier.ToString())).ToList())).ToList(),
            new ProgressValidationDto(
                f.Validation.Provenance, f.Validation.OriginMin, f.Validation.OriginMax, f.Validation.Centres,
                f.Validation.Metrics.Select(m => new ProgressHorizonMetricDto(
                    m.Predictor, m.Horizon, m.N, Round(m.MaePp), San(m.CoverageP10P90))).ToList(),
                f.Validation.Bands.Select(b => new ProgressBandDto(b.Horizon, Round(b.P10), Round(b.P90), b.N)).ToList(),
                f.Validation.Notes)));
    }

    /// <summary>
    /// The cost-centre EVM panel at any period, measured or projected — what the take-off tab reads so
    /// its figures keep moving past the last reported period instead of freezing at the origin.
    ///
    /// At or below the origin this is a passthrough of the reported rows, identical to
    /// <c>/api/v1/cost-centres</c>, so the caller has one shape either side of the boundary. Past it,
    /// EV comes from the progress projection through the schema's own <c>BAC × pct</c> identity and AC
    /// from the incremental-spend cone; PV and SPI are null, because the baseline curve ends at the
    /// origin and no planned value is invented for a period that has none.
    ///
    /// <paramref name="bcc"/> is a comma-separated centre list; omit it for every centre.
    /// </summary>
    [HttpGet("panel")]
    public async Task<IActionResult> Panel([FromQuery] int? period, [FromQuery] string? bcc, CancellationToken ct)
    {
        var (snap, err) = await Resolve(ct); if (err is not null) return err;

        var projector = new EvmProjector(snap!.Panel, snap.ProgressForecaster, snap.Forecaster, snap.ForecastPeriod);
        int p = period ?? snap.ForecastPeriod;

        if (p < projector.MinPeriod || p > projector.MaxPeriod)
            return BadRequest(new { error = $"period must be in [{projector.MinPeriod}, {projector.MaxPeriod}]." });
        if (p > projector.OriginPeriod && !projector.CanProject) return NoForecast();

        var ids = bcc?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var panel = projector.ProjectAt(p, ids);

        return Ok(new ProjectedPanelDto(
            panel.Period, panel.OriginPeriod, panel.HorizonPeriod,
            panel.BacktestedThroughPeriod, panel.SpendBacktestedThroughPeriod,
            panel.Basis.ToString(), panel.Method,
            panel.PvAvailable, panel.PvReason, panel.Notes,
            panel.Centres.Select(c => new ProjectedCentreDto(
                c.BccId, c.PeriodId, c.Basis.ToString(),
                c.Discipline, c.PackageCode, c.Lifecycle,
                Money(c.Bac),
                Round(c.PctComplete), San(c.PctP10), San(c.PctP90),
                Money(c.Ev), Cash(c.EvP10), Cash(c.EvP90),
                Cash(c.Ac), Cash(c.AcP10), Cash(c.AcP90), c.AcAvailable, c.AcNote,
                Cash(c.Cv), San(c.Cpi), Cash(c.Eac), Cash(c.Vac), San(c.PctBudgetConsumed),
                Cash(c.Pv), San(c.Spi), San(c.PlannedPct),
                c.AlertLevel, c.AlertProjected,
                c.ProjectedFinishPeriod, Round(c.PacePctPerPeriod), c.Stalled)).ToList()));
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

    // Amounts as decimal at 2dp, matching how the dashboard serialises money. `Cash` keeps null
    // meaningful — an unavailable AC must not serialise as a confident zero.
    private static decimal Money(double v) => Math.Round((decimal)(double.IsFinite(v) ? v : 0), 2);
    private static decimal? Cash(double? v) => v is double d && double.IsFinite(d) ? Math.Round((decimal)d, 2) : null;
}
