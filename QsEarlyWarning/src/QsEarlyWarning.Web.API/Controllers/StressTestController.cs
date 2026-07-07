using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Core.StressTest;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

/// <summary>
/// Idea-3 Estimate Assumption Stress Test, served read-only from the project snapshot. Three explicitly
/// separated output classes: reconciliation tie-out (Class 1 — correctness proof), estimate-side
/// assumption flags (Class 2 — day-zero review prompts), and a RETROSPECTIVE gated peer benchmark
/// (Class 3). When the project has no estimate workbook, each endpoint returns available:false.
/// </summary>
[ApiController]
[Route("api/v1/stress-test")]
public sealed class StressTestController : ControllerBase
{
    private readonly IProjectSnapshotRegistry _registry;
    private readonly ProjectDirectory _directory;
    private readonly TenantContext _ctx;

    public StressTestController(IProjectSnapshotRegistry registry, ProjectDirectory directory, TenantContext ctx)
    {
        _registry = registry;
        _directory = directory;
        _ctx = ctx;
    }

    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation(CancellationToken ct)
    {
        var (snap, err) = await Resolve(ct); if (err is not null) return err;
        var st = snap!.StressTest;
        if (st is null)
            return Ok(new ReconciliationDto(false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                Array.Empty<ReconciliationItemDto>(), NoEstimateNote()));

        var r = st.Reconciliation;
        var failed = r.Items.Where(i => !i.TiesOut).Select(MapItem).ToList();
        return Ok(new ReconciliationDto(true, r.TiesOut, r.ItemsChecked, r.ItemsFailed,
            Round(r.ProjectDirectDelta), Round(r.ProjectUpliftDelta), Round(r.TotalDirectCost),
            Round(r.TotalIndirectCost), Round(r.TotalContractAmt), Round(r.TotalMargin), Round(r.TotalContingency),
            failed, st.Notes));
    }

    [HttpGet("assumptions")]
    public async Task<IActionResult> Assumptions([FromQuery] string? discipline, CancellationToken ct)
    {
        var (snap, err) = await Resolve(ct); if (err is not null) return err;
        var st = snap!.StressTest;
        if (st is null)
            return Ok(new AssumptionsDto(false, Array.Empty<PackageHeatDto>(), Array.Empty<AssumptionFlagDto>(), NoEstimateNote()));

        bool Keep(string? d) => discipline is null || string.Equals(d, discipline, StringComparison.OrdinalIgnoreCase);
        var heat = st.PackageHeat.Where(h => Keep(h.Discipline))
            .Select(h => new PackageHeatDto(h.Package, h.Discipline, h.FlagCount, h.HighCount, h.Severity)).ToList();
        var flags = st.AssumptionFlags.Where(f => Keep(f.Discipline))
            .Select(f => new AssumptionFlagDto(f.Package, f.Discipline, f.SubTrade, f.Unit, f.ResourceType,
                f.Kind, f.Severity, f.Reason, f.CohortN, f.RulesVersion, f.DrivingResourceLine)).ToList();
        return Ok(new AssumptionsDto(true, heat, flags, st.Notes));
    }

    [HttpGet("peer-benchmark")]
    public async Task<IActionResult> PeerBenchmark(CancellationToken ct)
    {
        var (snap, err) = await Resolve(ct); if (err is not null) return err;
        var st = snap!.StressTest;
        if (st is null)
            return Ok(new PeerBenchmarkResponseDto(false, true, true, Array.Empty<PeerBenchmarkDto>(), NoEstimateNote()));

        var benches = st.PeerBenchmarks.Select(b => new PeerBenchmarkDto(
            b.Package, b.Unit, b.ResourceType, b.ProcurementRoute, b.SubTradeAdvisory,
            Round(b.EstimatedUnitCost), San(b.PeerMedian), San(b.PeerBandLow), San(b.PeerBandHigh),
            b.PeerCount, San(b.DeltaPct), b.Status)).ToList();
        return Ok(new PeerBenchmarkResponseDto(true, Retrospective: true, st.Class3NoCellMeetsMinPeers, benches, st.Notes));
    }

    private static ReconciliationItemDto MapItem(ReconciliationResult i) => new(
        i.Scope, i.QuantityReDerivationOk, i.ResourceCostIdentityOk, i.RepeatedContractAmtConsistent,
        i.DirectTieOutOk, i.ContractUpliftOk, Round(i.DirectTieOutDelta), Round(i.ContractUpliftDelta),
        i.Failures.Select(f => new ReconciliationFailureDto(
            f.Scope, f.Check, f.Line, Round(f.Actual), Round(f.Expected), Round(f.Delta), Round(f.Tolerance))).ToList());

    private static string[] NoEstimateNote() =>
        new[] { "No estimate workbook for this project — the stress test runs only on the estimate's owning project." };

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
