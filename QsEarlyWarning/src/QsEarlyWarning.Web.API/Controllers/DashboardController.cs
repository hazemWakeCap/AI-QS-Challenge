using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

/// <summary>Live EVM read-side for the dashboard, served from Postgres for the selected project.</summary>
[ApiController]
[Route("api/v1")]
public sealed class DashboardController : ControllerBase
{
    private readonly IProjectSnapshotRegistry _registry;
    private readonly ProjectDirectory _directory;
    private readonly TenantContext _ctx;

    public DashboardController(IProjectSnapshotRegistry registry, ProjectDirectory directory, TenantContext ctx)
    {
        _registry = registry;
        _directory = directory;
        _ctx = ctx;
    }

    /// <summary>Project EVM totals for a period + the full period-by-period trend.</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<EvmOverviewDto>> Overview([FromQuery] int? period, CancellationToken ct = default)
    {
        var ctx = await Resolve(ct);
        if (ctx.Error is not null) return ctx.Error;
        var (project, snapshot) = (ctx.Project!, ctx.Snapshot!);

        var periods = snapshot.Panel.Select(p => p.PeriodId).Distinct().OrderBy(x => x).ToList();
        var p = period ?? snapshot.ForecastPeriod;
        if (!periods.Contains(p))
            return BadRequest($"period must be one of {periods[0]}..{snapshot.ForecastPeriod}.");

        var totals = Totals(snapshot.Panel, p, project.ReportingCurrency);
        var trend = periods.Select(per =>
        {
            var t = Totals(snapshot.Panel, per, project.ReportingCurrency);
            return new EvmTrendPointDto(per, t.Pv, t.Ev, t.Ac, t.Cpi, t.Spi);
        }).ToList();

        return Ok(new EvmOverviewDto(project.Slug, p, snapshot.MinPeriod, snapshot.ForecastPeriod, totals, trend));
    }

    /// <summary>Per-cost-centre computed EVM for a period (the grid).</summary>
    [HttpGet("cost-centres")]
    public async Task<ActionResult<IReadOnlyList<CostCentreEvmDto>>> CostCentres([FromQuery] int? period, CancellationToken ct = default)
    {
        var ctx = await Resolve(ct);
        if (ctx.Error is not null) return ctx.Error;
        var snapshot = ctx.Snapshot!;
        var p = period ?? snapshot.ForecastPeriod;

        var rows = snapshot.Panel
            .Where(r => r.PeriodId == p)
            .OrderBy(r => r.BccId, StringComparer.Ordinal)
            .Select(r => new CostCentreEvmDto(
                r.BccId, r.Discipline, r.PackageCode,
                LifecycleOf(r.AlertLevel), r.AlertLevel ?? "GREEN",
                Money(r.BacAed), r.PlanPctComplete, r.ActualPctComplete,
                Money(r.PvAed), Money(r.EvAed), Money(r.AcCumulative),
                r.Cpi, r.Spi, Money(r.EacAed), Money(r.VacAed), r.PctBudgetConsumed))
            .ToList();

        return Ok(rows);
    }

    // ── shared resolution: authenticated + member of the selected project → build snapshot ──
    private async Task<(ProjectInfo? Project, ProjectSnapshot? Snapshot, ActionResult? Error)> Resolve(CancellationToken ct)
    {
        if (!_ctx.IsAuthenticated)
            return (null, null, Unauthorized("Provide X-User-Id and X-Project-Slug."));

        var mine = await _directory.ListForUserAsync(_ctx.UserId!.Value, ct);
        var project = mine.FirstOrDefault(p => p.Slug == _ctx.ProjectSlug);
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

    private static EvmTotalsDto Totals(IReadOnlyList<CostCentrePeriod> panel, int period, string currency)
    {
        var rows = panel.Where(r => r.PeriodId == period).ToList();
        double bac = rows.Sum(r => r.BacAed ?? 0);
        double pv = rows.Sum(r => r.PvAed ?? 0);
        double ev = rows.Sum(r => r.EvAed ?? 0);
        double ac = rows.Sum(r => r.AcCumulative ?? 0);
        double? cpi = ac != 0 ? ev / ac : null;
        double? spi = pv != 0 ? ev / pv : null;
        double eac = ev != 0 ? bac * ac / ev : bac;   // CPI-method; fall back to BAC when nothing earned
        double vac = bac - eac;
        double? pct = bac != 0 ? 100.0 * ac / bac : null;
        int amber = rows.Count(r => string.Equals(r.AlertLevel, "AMBER", StringComparison.OrdinalIgnoreCase));
        return new EvmTotalsDto(period, currency, Money(bac), Money(pv), Money(ev), Money(ac),
            Money(ev - ac), cpi, spi, Money(eac), Money(vac), pct, rows.Count, amber);
    }

    private static string LifecycleOf(string? alert) => (alert ?? "").ToUpperInvariant() switch
    {
        "NOT STARTED" => "NOT_STARTED",
        "CLOSED" => "CLOSED",
        _ => "IN_PROGRESS",
    };

    private static decimal Money(double? v) => Math.Round((decimal)(v ?? 0), 2);
}
