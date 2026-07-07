using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

/// <summary>The authorized-write workflow: period open/close, monthly progress capture, estimate
/// publish. Each write is scoped to the caller's selected project (RLS + procedure membership check),
/// and refreshes the project snapshot so subsequent reads reflect the change.</summary>
[ApiController]
[Route("api/v1")]
public sealed class WorkflowController : ControllerBase
{
    private readonly TenantWriteService _writes;
    private readonly IProjectSnapshotRegistry _registry;
    private readonly ProjectDirectory _directory;
    private readonly TenantContext _ctx;

    public WorkflowController(TenantWriteService writes, IProjectSnapshotRegistry registry,
                              ProjectDirectory directory, TenantContext ctx)
    {
        _writes = writes;
        _registry = registry;
        _directory = directory;
        _ctx = ctx;
    }

    [HttpGet("periods")]
    public async Task<ActionResult<IReadOnlyList<PeriodDto>>> Periods(CancellationToken ct = default)
    {
        var r = await ResolveProject(ct);
        if (r.Error is not null) return r.Error;
        var periods = await _writes.ListPeriodsAsync(r.ProjectId, _ctx.UserId!.Value, ct);
        return Ok(periods.Select(p => new PeriodDto(p.Id, p.PeriodId, p.PeriodStart, p.Status, p.OpenedAt, p.ClosedAt)).ToList());
    }

    [HttpPost("periods/{ordinal:int}/open")]
    public Task<IActionResult> OpenPeriod(int ordinal, CancellationToken ct = default)
        => Write(ct, (pid, uid) => _writes.OpenPeriodAsync(pid, uid, ordinal, ct));

    [HttpPost("periods/{ordinal:int}/close")]
    public Task<IActionResult> ClosePeriod(int ordinal, CancellationToken ct = default)
        => Write(ct, (pid, uid) => _writes.ClosePeriodAsync(pid, uid, ordinal, ct));

    [HttpPost("capture/progress")]
    public Task<IActionResult> CaptureProgress([FromBody] CaptureProgressRequest req, CancellationToken ct = default)
        => Write(ct, async (pid, uid) =>
        {
            var n = await _writes.CaptureProgressAsync(pid, uid, req.BccId, req.Period, req.ActualPct, ct);
            if (n == 0) throw new TenantWriteException($"no open fact for {req.BccId} at period {req.Period}");
        });

    [HttpPost("estimate-versions/{versionId:long}/publish")]
    public Task<IActionResult> Publish(long versionId, CancellationToken ct = default)
        => Write(ct, (pid, uid) => _writes.PublishVersionAsync(pid, uid, versionId, ct));

    [HttpPost("capture/cost")]
    public Task<IActionResult> CaptureCost([FromBody] CaptureCostRequest req, CancellationToken ct = default)
        => Write(ct, (pid, uid) => _writes.PostCostDeltaAsync(pid, uid, req.BccId, req.Period, req.Rtype, req.Amount, req.Direction, req.IdempotencyKey, ct));

    [HttpPost("periods/{ordinal:int}/rebaseline")]
    public Task<IActionResult> Rebaseline(int ordinal, CancellationToken ct = default)
        => Write(ct, (pid, uid) => _writes.RebaselinePeriodAsync(pid, uid, ordinal, ct));

    [HttpPost("cutover")]
    public Task<IActionResult> Cutover(CancellationToken ct = default)
        => Write(ct, (pid, uid) => _writes.CutoverToLedgerAsync(pid, uid, ct));

    // ── shared: authorize, run the write, refresh the snapshot, map errors ──
    private async Task<IActionResult> Write(CancellationToken ct, Func<long, long, Task> action)
    {
        var r = await ResolveProject(ct);
        if (r.Error is not null) return r.Error;
        try
        {
            await action(r.ProjectId, _ctx.UserId!.Value);
            await _registry.RebuildAsync(r.ProjectId, _ctx.UserId.Value, ct);   // reflect the write in reads
            return Ok(new { ok = true });
        }
        catch (TenantWriteException ex)
        {
            return Conflict(new { ok = false, error = ex.Message });
        }
    }

    private async Task<(long ProjectId, ActionResult? Error)> ResolveProject(CancellationToken ct)
    {
        if (!_ctx.IsAuthenticated)
            return (0, Unauthorized("Provide X-User-Id and X-Project-Slug."));
        var mine = await _directory.ListForUserAsync(_ctx.UserId!.Value, ct);
        var project = mine.FirstOrDefault(p => p.Slug == _ctx.ProjectSlug);
        if (project is null)
            return (0, StatusCode(StatusCodes.Status403Forbidden,
                $"User {_ctx.UserId} is not a member of project '{_ctx.ProjectSlug}'."));
        return (project.Id, null);
    }
}
