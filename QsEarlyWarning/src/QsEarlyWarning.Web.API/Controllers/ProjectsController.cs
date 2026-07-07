using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

[ApiController]
[Route("api/v1/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly ProjectDirectory _directory;
    private readonly TenantContext _ctx;

    public ProjectsController(ProjectDirectory directory, TenantContext ctx)
    {
        _directory = directory;
        _ctx = ctx;
    }

    /// <summary>Projects the authenticated caller is a member of (the tenant switcher). Needs only
    /// X-User-Id — no project is selected yet.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProjectDto>>> Get(CancellationToken ct = default)
    {
        if (_ctx.UserId is not > 0)
            return Unauthorized("Provide X-User-Id.");

        var projects = await _directory.ListForUserAsync(_ctx.UserId.Value, ct);
        return Ok(projects
            .Select(p => new ProjectDto(p.Id, p.Slug, p.Name, p.ReportingCurrency, p.ActiveEstimateVersionId, p.LedgerActive))
            .ToList());
    }
}
