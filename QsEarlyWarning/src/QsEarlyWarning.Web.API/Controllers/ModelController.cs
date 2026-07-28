using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Model;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

/// <summary>
/// The spatial read-side: cost rolled up to the physical zones of the building, and the derived
/// massing spec the viewer draws.
///
/// <para>Phase 1 could say WHICH cost centre was drifting but never WHERE. `Zone_Area` — the only
/// spatial attribute in the dataset — was read and discarded. These two endpoints are what let the
/// watchlist be painted onto a building.</para>
/// </summary>
[ApiController]
[Route("api/v1/model")]
public sealed class ModelController : ControllerBase
{
    /// <summary>
    /// Share of a zone's BAC that must have been spent before its CPI is worth painting.
    /// Below this the ratio is real arithmetic but rests on too little money to carry the
    /// confidence a coloured building implies.
    /// </summary>
    private const double MaterialityFloor = 0.01;

    private readonly IProjectSnapshotRegistry _registry;
    private readonly ProjectDirectory _directory;
    private readonly TenantContext _ctx;

    public ModelController(IProjectSnapshotRegistry registry, ProjectDirectory directory, TenantContext ctx)
    {
        _registry = registry;
        _directory = directory;
        _ctx = ctx;
    }

    /// <summary>Per-zone cost rollup for a period, with an explicit unmapped residual.</summary>
    [HttpGet("cost-map")]
    public async Task<ActionResult<CostMapDto>> CostMap([FromQuery] int? period, CancellationToken ct = default)
    {
        var res = await Resolve(ct);
        if (res.Error is not null) return res.Error;
        var (project, snapshot) = (res.Project!, res.Snapshot!);

        var periods = snapshot.Panel.Select(r => r.PeriodId).Distinct().OrderBy(x => x).ToList();
        var p = period ?? snapshot.ForecastPeriod;
        if (!periods.Contains(p))
            return BadRequest($"period must be one of {periods[0]}..{snapshot.ForecastPeriod}.");

        var rows = snapshot.Panel.Where(r => r.PeriodId == p).ToList();

        // Un-located centres are held out and reported as a residual rather than being spread
        // across zones — the data carries no allocation basis, and inventing one would put
        // fabricated money on a picture of a building.
        var located = rows.Where(r => !string.IsNullOrWhiteSpace(r.ZoneArea)).ToList();
        var unmapped = rows.Where(r => string.IsNullOrWhiteSpace(r.ZoneArea)).ToList();

        var zones = located
            .GroupBy(r => r.ZoneArea!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => BuildZone(g.Key, g.ToList()))
            .OrderByDescending(z => z.Bac)
            .ToList();

        decimal projectBac = Money(rows.Sum(r => r.BacAed ?? 0));
        decimal projectAc = Money(rows.Sum(r => r.AcCumulative ?? 0));
        decimal unmappedBac = Money(unmapped.Sum(r => r.BacAed ?? 0));

        // Only zones we are confident enough to call drifting count toward the headline.
        decimal unspentDrifting = zones
            .Where(z => z.CostSufficient && z.Cpi is { } c && c < EvmThresholds.CpiThreshold)
            .Sum(z => z.Unspent);

        return Ok(new CostMapDto(
            project.Slug, p, snapshot.MinPeriod, snapshot.ForecastPeriod, project.ReportingCurrency,
            projectBac, projectAc, unmappedBac, unmapped.Count, unspentDrifting, zones));
    }

    /// <summary>The Tower X massing spec, derived from priced BOQ lines, with its provenance.</summary>
    [HttpGet("geometry-spec")]
    public async Task<ActionResult<GeometrySpecDto>> GeometrySpec(CancellationToken ct = default)
    {
        var res = await Resolve(ct);
        if (res.Error is not null) return res.Error;
        var (project, snapshot) = (res.Project!, res.Snapshot!);

        // Derived once at snapshot build (the registry owns estimate access; controllers never
        // touch raw estimate rows). Null means this project does not own the estimate.
        var spec = snapshot.Geometry;
        if (spec is null)
            return NotFound($"No derived geometry for project '{project.Slug}' (no estimate loaded).");

        return Ok(new GeometrySpecDto(
            project.Slug, spec.FloorCount, spec.BasementLevels,
            spec.FootprintWidthM, spec.FootprintDepthM, spec.FloorHeightM,
            spec.BasementDepthM, spec.CoreWidthM, spec.CoreDepthM,
            spec.Derived, spec.Provenance,
            spec.Dimensions.Select(d => new GeometryDimensionDto(
                d.Key, d.Label, d.Value, d.Unit, d.SourceItemRef, d.SourceDescription, d.Derivation)).ToList()));
    }

    /// <summary>Prices a take-off measured off any model with this project's unit-rate library.</summary>
    [HttpPost("price-takeoff")]
    public async Task<ActionResult<TakeoffPricingDto>> PriceTakeoff(
        [FromBody] PriceTakeoffRequest request, CancellationToken ct = default)
    {
        var res = await Resolve(ct);
        if (res.Error is not null) return res.Error;
        var (project, snapshot) = (res.Project!, res.Snapshot!);

        if (request?.Lines is null || request.Lines.Count == 0)
            return BadRequest("Provide at least one measured line.");
        if (request.Lines.Count > 500)
            return BadRequest("Too many lines; aggregate by IFC class before pricing.");

        var rates = snapshot.Rates;
        if (rates is null)
            return NotFound($"Project '{project.Slug}' has no rate library (no estimate loaded).");

        var lines = new List<TakeoffLine>(request.Lines.Count);
        foreach (var l in request.Lines)
        {
            if (string.IsNullOrWhiteSpace(l.IfcClass))
                return BadRequest("Every line needs an ifcClass.");
            if (!TryMeasure(l.Measure, out var measure))
                return BadRequest($"measure must be 'volume' or 'area'; got '{l.Measure}'.");
            if (!double.IsFinite(l.Quantity))
                return BadRequest($"Quantity for {l.IfcClass} is not a finite number.");

            lines.Add(new TakeoffLine(
                l.IfcClass.Trim().ToUpperInvariant(), measure, l.Quantity,
                Math.Max(0, l.ElementCount), Math.Max(0, l.UnmeasuredCount)));
        }

        var result = TakeoffPricer.Price(lines, rates, project.ReportingCurrency, request.ModelElementCount);

        return Ok(new TakeoffPricingDto(
            project.Slug,
            result.Currency,
            Math.Round((decimal)result.PricedAmount, 2),
            result.Priced.Select(p => new PricedLineDto(
                p.IfcClass, Label(p.Measure), p.Quantity, p.Unit, p.ElementCount,
                p.BoqItemRef, p.BoqDescription, p.UnitRate, Math.Round((decimal)p.Amount, 2), p.Rationale)).ToList(),
            result.Unpriced.Select(u => new UnpricedLineDto(
                u.IfcClass, Label(u.Measure), u.Quantity, u.ElementCount, u.Reason)).ToList(),
            result.TotalElements, result.PricedElements, result.UnpricedElements, result.UnmeasuredElements,
            result.TiesOut,
            result.RulesApplied.Select(r => new TakeoffRuleDto(
                r.IfcClass, Label(r.Measure), r.Unit, r.BoqItemRef, r.Rationale)).ToList(),
            RateBasis: "Direct + indirect unit cost from this project's BOQ. Margin and contingency "
                     + "are excluded — they are commercial positions taken per project, not transferable rates."));
    }

    private static bool TryMeasure(string? value, out TakeoffMeasure measure)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "volume": measure = TakeoffMeasure.Volume; return true;
            case "area": measure = TakeoffMeasure.Area; return true;
            default: measure = TakeoffMeasure.Volume; return false;
        }
    }

    private static string Label(TakeoffMeasure m) => m == TakeoffMeasure.Volume ? "volume" : "area";

    // ── zone rollup ───────────────────────────────────────────────────────────────

    private static ZoneCostDto BuildZone(string zoneCode, IReadOnlyList<CostCentrePeriod> centres)
    {
        double bac = centres.Sum(r => r.BacAed ?? 0);
        double pv = centres.Sum(r => r.PvAed ?? 0);
        double ev = centres.Sum(r => r.EvAed ?? 0);
        double ac = centres.Sum(r => r.AcCumulative ?? 0);

        // Aggregate CPI is ΣEV/ΣAC — never the mean of per-centre CPIs, which would let a tiny
        // centre with a wild ratio outvote the money. Same rule the copilot prompt already states.
        bool costSufficient = ac > 0 && bac > 0 && ac / bac >= MaterialityFloor;
        double? cpi = costSufficient ? ev / ac : null;
        double? spi = pv > 0 ? ev / pv : null;

        int amber = centres.Count(r => Is(r.AlertLevel, "AMBER"));
        bool allDormant = centres.All(r => Is(r.AlertLevel, "NOT STARTED"));

        var alert =
            allDormant ? "NOT_STARTED"
            : !costSufficient ? "INSUFFICIENT_COST"
            : cpi < EvmThresholds.CpiThreshold ? "AMBER"
            : "GREEN";

        // Click-through target: the worst centre by CPI among those actually spending money.
        var worst = centres
            .Where(r => r.Cpi is { } c && double.IsFinite(c) && (r.AcCumulative ?? 0) > 0)
            .OrderBy(r => r.Cpi!.Value)
            .FirstOrDefault();

        return new ZoneCostDto(
            zoneCode, Money(bac), Money(pv), Money(ev), Money(ac), Money(bac - ac),
            cpi, spi, costSufficient, alert,
            centres.Count, amber, worst?.BccId, worst?.Cpi);
    }

    private static bool Is(string? alert, string value) =>
        string.Equals(alert, value, StringComparison.OrdinalIgnoreCase);

    private static decimal Money(double v) => Math.Round((decimal)v, 2);

    // ── shared resolution: authenticated + member of the selected project → snapshot ──
    // Mirrors DashboardController.Resolve (the ProjectDirectory pattern), not the
    // ProjectResolver + RLS-probe pattern used by the watchlist controllers.
    private async Task<(ProjectInfo? Project, ProjectSnapshot? Snapshot, ActionResult? Error)> Resolve(CancellationToken ct)
    {
        if (!_ctx.IsAuthenticated)
            return (null, null, Unauthorized("Provide X-User-Id and X-Project-Slug."));

        var mine = await _directory.ListForUserAsync(_ctx.UserId!.Value, ct);
        var project = mine.FirstOrDefault(x => x.Slug == _ctx.ProjectSlug);
        if (project is null)
            return (null, null, StatusCode(StatusCodes.Status403Forbidden,
                $"User {_ctx.UserId} is not a member of project '{_ctx.ProjectSlug}' (or it does not exist)."));

        try
        {
            var snapshot = await _registry.GetOrBuildAsync(project.Id, _ctx.UserId.Value, ct);
            return (project, snapshot, null);
        }
        catch (InvalidOperationException)
        {
            return (null, null, StatusCode(StatusCodes.Status404NotFound,
                $"No data for project '{_ctx.ProjectSlug}' yet (not imported)."));
        }
    }
}
