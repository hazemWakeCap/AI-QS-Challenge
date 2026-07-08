using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Infrastructure.Excel;
using QsEarlyWarning.Infrastructure.Import;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

/// <summary>Create-empty request (JSON).</summary>
public sealed record CreateProjectRequest(string Name, string Slug, string Currency);

/// <summary>Metadata patch (JSON); null fields are left unchanged.</summary>
public sealed record UpdateProjectRequest(string? Name, string? Currency);

[ApiController]
[Route("api/v1/projects")]
public sealed class ProjectsController : ControllerBase
{
    // Cap uploads at ~50 MB (the Tower-X workbook is well under 2 MB).
    private const long MaxUploadBytes = 50_000_000;

    private readonly ProjectDirectory _directory;
    private readonly ProjectAdminService _admin;
    private readonly ProjectImportService _import;
    private readonly ProjectResolver _resolver;
    private readonly IProjectSnapshotRegistry _registry;
    private readonly TenantContext _ctx;

    public ProjectsController(
        ProjectDirectory directory, ProjectAdminService admin, ProjectImportService import,
        ProjectResolver resolver, IProjectSnapshotRegistry registry, TenantContext ctx)
    {
        _directory = directory;
        _admin = admin;
        _import = import;
        _resolver = resolver;
        _registry = registry;
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

    /// <summary>Create an empty project (no data) owned by the caller.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProjectRequest req, CancellationToken ct = default)
    {
        if (_ctx.UserId is not > 0) return Unauthorized("Provide X-User-Id.");
        var invalid = ValidateMeta(req.Slug, req.Name, req.Currency, out var slug, out var currency);
        if (invalid is not null) return BadRequest(new { error = invalid });
        try
        {
            var id = await _admin.CreateEmptyAsync(slug, new ProjectMeta(req.Name.Trim(), currency, _ctx.UserId.Value), ct);
            return Ok(new ProjectDto(id, slug, req.Name.Trim(), currency, null, false));
        }
        catch (ProjectExistsException ex) { return Conflict(new { error = ex.Message }); }
    }

    /// <summary>Create a project by uploading a workbook (multipart): project row + owner + full data ingest.</summary>
    [HttpPost("import")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> ImportNew(
        [FromForm] string name, [FromForm] string slug, [FromForm] string currency,
        IFormFile? file, CancellationToken ct = default)
    {
        if (_ctx.UserId is not > 0) return Unauthorized("Provide X-User-Id.");
        var invalid = ValidateMeta(slug, name, currency, out var s, out var cur);
        if (invalid is not null) return BadRequest(new { error = invalid });
        if (file is null || file.Length == 0) return BadRequest(new { error = "No workbook file uploaded." });
        // The importer purges any prior load of a slug; for create-new we refuse to clobber an existing one.
        if (await _resolver.ResolveAsync(s, ct) is not null)
            return Conflict(new { error = $"Project '{s}' already exists." });

        return await RunImport(file, s, name.Trim(), cur, _ctx.UserId.Value, ct);
    }

    /// <summary>Re-import / refresh an existing project from a workbook (multipart). Destructive: replaces the
    /// project's data. Metadata (name / currency / owner) is preserved.</summary>
    [HttpPost("{slug}/import")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Reimport(string slug, IFormFile? file, CancellationToken ct = default)
    {
        var error = await Authorize(slug, ct);
        if (error is not null) return error;
        if (file is null || file.Length == 0) return BadRequest(new { error = "No workbook file uploaded." });
        var meta = await _admin.GetMetaAsync(slug, ct);
        if (meta is null) return NotFound(new { error = $"Unknown project '{slug}'." });

        // The importer purges + recreates the project (owner membership only). Snapshot all memberships
        // first and restore them after, so editors/viewers/service users aren't silently dropped.
        var members = await _admin.GetMembershipsAsync(slug, ct);
        var result = await RunImport(file, slug, meta.Name, meta.Currency, meta.OwnerUserId, ct);
        await _admin.RestoreMembershipsAsync(slug, members, ct);
        return result;
    }

    /// <summary>Rename / change reporting currency.</summary>
    [HttpPatch("{slug}")]
    public async Task<IActionResult> Update(string slug, [FromBody] UpdateProjectRequest req, CancellationToken ct = default)
    {
        var error = await Authorize(slug, ct);
        if (error is not null) return error;

        string? currency = null;
        if (!string.IsNullOrWhiteSpace(req.Currency))
        {
            currency = req.Currency.Trim().ToUpperInvariant();
            if (!Regex.IsMatch(currency, "^[A-Z]{3}$")) return BadRequest(new { error = "Currency must be a 3-letter code." });
        }
        var name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim();
        if (name is null && currency is null) return BadRequest(new { error = "Nothing to update." });

        bool ok;
        try { ok = await _admin.UpdateMetaAsync(slug, name, currency, ct); }
        // e.g. reporting_currency is immutable once monetary data exists (check-constraint 23514) — a
        // domain rule, not a server fault: surface it as a 409 with the DB message.
        catch (Npgsql.PostgresException ex) { return Conflict(new { error = ex.MessageText }); }
        _resolver.Invalidate(slug);
        return ok ? Ok(new { ok = true }) : NotFound(new { error = $"Unknown project '{slug}'." });
    }

    /// <summary>Delete a project and all of its data.</summary>
    [HttpDelete("{slug}")]
    public async Task<IActionResult> Delete(string slug, CancellationToken ct = default)
    {
        var error = await Authorize(slug, ct);
        if (error is not null) return error;

        var ok = await _admin.DeleteAsync(slug, ct);
        _resolver.Invalidate(slug);
        return ok ? Ok(new { ok = true }) : NotFound(new { error = $"Unknown project '{slug}'." });
    }

    // ── helpers ──

    /// <summary>Persist the upload to a temp file, run the importer, refresh the snapshot, map errors,
    /// and return a JSON reconciliation summary.</summary>
    private async Task<IActionResult> RunImport(IFormFile file, string slug, string name, string currency, long owner, CancellationToken ct)
    {
        var tmp = Path.ChangeExtension(Path.GetTempFileName(), ".xlsx");
        try
        {
            await using (var fs = System.IO.File.Create(tmp))
                await file.CopyToAsync(fs, ct);

            ReconciliationReport report;
            try
            {
                report = await _import.ImportAsync(tmp, slug, $"user:{owner}", new ProjectMeta(name, currency, owner), ct);
            }
            catch (DataContractException ex) { return BadRequest(new { error = $"Invalid workbook: {ex.Message}" }); }
            catch (Npgsql.PostgresException ex) { return BadRequest(new { error = $"Import failed: {ex.MessageText}" }); }

            // The importer purges + re-inserts the project row, so its id changes — drop the stale cache entry
            // before resolving the new id to refresh the read snapshot.
            _resolver.Invalidate(slug);
            if (report.Activated)
            {
                var pid = await _resolver.ResolveAsync(slug, ct);
                if (pid is not null)
                    try { await _registry.RebuildAsync(pid.Value, owner, ct); } catch { /* builds on next read */ }
            }

            var summary = new
            {
                passed = report.Passed,
                activated = report.Activated,
                costCentres = report.CostCentres,
                periods = report.Periods,
                facts = report.Facts,
                failureReason = report.FailureReason,
                publishViolations = report.PublishViolations,
            };
            // Activation failed (e.g. publish-validation violations) → 422 with the reason.
            return report.Activated ? Ok(summary) : UnprocessableEntity(summary);
        }
        finally
        {
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Membership check for operations on an existing project (same pattern as WorkflowController).</summary>
    private async Task<IActionResult?> Authorize(string slug, CancellationToken ct)
    {
        if (_ctx.UserId is not > 0) return Unauthorized("Provide X-User-Id.");
        var mine = await _directory.ListForUserAsync(_ctx.UserId.Value, ct);
        if (mine.All(p => p.Slug != slug))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = $"User {_ctx.UserId} is not a member of project '{slug}'." });
        return null;
    }

    /// <summary>Normalize + validate create/import metadata. Returns an error string, or null when valid.</summary>
    private static string? ValidateMeta(string? slug, string? name, string? currency, out string normSlug, out string normCurrency)
    {
        normSlug = (slug ?? "").Trim().ToLowerInvariant();
        normCurrency = (currency ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(name)) return "Name is required.";
        if (!Regex.IsMatch(normSlug, "^[a-z0-9-]+$")) return "Slug must be lowercase letters, digits and hyphens.";
        if (!Regex.IsMatch(normCurrency, "^[A-Z]{3}$")) return "Currency must be a 3-letter ISO code (e.g. AED).";
        return null;
    }
}
