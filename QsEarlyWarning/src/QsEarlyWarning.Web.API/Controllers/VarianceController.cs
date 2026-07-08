using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Core.Variance;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

/// <summary>
/// Idea-5 Variance Attribution Bridge, served read-only. The drill-down behind idea-1's watchlist: for a
/// (bcc, period) it returns the CV cost/efficiency lane decomposed by resource + the monetary SV lane,
/// with the tie-out and the assumption-based-attribution badge. Uses the platform's strongest tenant
/// sequence (ProjectResolver → RLS IsAuthorizedAsync probe → registry), so a project-keyed snapshot cache
/// hit is authorized per-request.
/// </summary>
[ApiController]
[Route("api/v1/variance")]
public sealed class VarianceController : ControllerBase
{
    private readonly IProjectSnapshotRegistry _registry;
    private readonly IProjectPanelSource _panels;
    private readonly ProjectResolver _resolver;
    private readonly TenantContext _ctx;

    public VarianceController(
        IProjectSnapshotRegistry registry, IProjectPanelSource panels, ProjectResolver resolver, TenantContext ctx)
    {
        _registry = registry;
        _panels = panels;
        _resolver = resolver;
        _ctx = ctx;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string bcc, [FromQuery] int period, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bcc)) return BadRequest("bcc is required.");
        if (!_ctx.IsAuthenticated) return Unauthorized("Provide X-User-Id and X-Project-Slug.");

        var projectId = await _resolver.ResolveAsync(_ctx.ProjectSlug!, ct);
        if (projectId is null) return NotFound(new { error = $"unknown project '{_ctx.ProjectSlug}'." });
        if (!await _panels.IsAuthorizedAsync(projectId.Value, _ctx.UserId!.Value, ct))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"not a member of '{_ctx.ProjectSlug}'." });

        ProjectSnapshot snap;
        try { snap = await _registry.GetOrBuildAsync(projectId.Value, _ctx.UserId!.Value, ct); }
        catch (InvalidOperationException) { return NotFound(new { error = "no data for this project yet." }); }

        var b = new VarianceAttributor().Attribute(snap.Panel, snap.ResourceMix, bcc, period);
        return Ok(new VarianceBridgeDto(
            b.BccId, b.PeriodId, b.Available, b.UnavailableReason, b.Package, b.Discipline,
            San(b.Bac), San(b.Pv), San(b.Ev), San(b.Ac), San(b.CvAed), San(b.SvAed), San(b.Spi),
            b.Contributions.Select(c => new ResourceContributionDto(
                c.ResourceType, San(c.NormShare) ?? 0, San(c.EvR) ?? 0, San(c.AcR) ?? 0, San(c.CvR) ?? 0,
                San(c.TimesNormBudget))).ToList(),
            b.DominantResourceType, San(b.UnexplainedResidual), b.TiesOut, b.ResourceBreakdownAvailable,
            b.AssumptionBased, b.EvidenceNeeded, b.Notes));
    }

    private static double? San(double? v) => v is double d && double.IsFinite(d) ? Math.Round(d, 3) : null;
}
