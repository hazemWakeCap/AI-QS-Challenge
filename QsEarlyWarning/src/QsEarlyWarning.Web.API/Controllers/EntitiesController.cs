using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Infrastructure.Crud;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

/// <summary>Governed generic CRUD over the tenant tables (plan: "CRUD for all tables"). The database
/// enforces every invariant; this controller resolves the tenant, dispatches to
/// <see cref="GenericCrudService"/>, and maps rejections to HTTP status.</summary>
[ApiController]
[Route("api/v1/entities")]
public sealed class EntitiesController : ControllerBase
{
    private readonly GenericCrudService _crud;
    private readonly ProjectDirectory _directory;
    private readonly TenantContext _ctx;

    public EntitiesController(GenericCrudService crud, ProjectDirectory directory, TenantContext ctx)
    {
        _crud = crud;
        _directory = directory;
        _ctx = ctx;
    }

    /// <summary>The entity registry — keys, display names, column metadata and capabilities — so the UI
    /// can auto-generate grids and forms. Static metadata; no tenant scoping needed.</summary>
    [HttpGet]
    public ActionResult<object> Registry() => Ok(EntityRegistry.All.Select(e => new
    {
        e.Key, e.Display, e.Table, naturalKey = e.NaturalKey, caps = e.Caps,
        // workbook grouping/lineage for the sheet-first Data-Admin nav (see EntityDescriptor)
        e.Group, e.GroupLabel, e.GroupOrder, e.SheetRef, e.Blurb, e.Order,
        columns = e.Columns.Select(c => new
        {
            c.Name, kind = c.Kind.ToString(), c.Insertable, c.Updatable, c.Required, fkEntity = c.FkEntity, @enum = c.Enum,
        }),
    }));

    [HttpGet("{key}")]
    public async Task<IActionResult> List(string key, CancellationToken ct)
    {
        var (e, pid, err) = await Resolve(key, ct); if (err is not null) return err;
        if (!e!.Caps.List) return NotFound();
        var filters = Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString());
        return await Guard(() => _crud.ListAsync(e, pid, _ctx.UserId!.Value, filters, ct));
    }

    [HttpGet("{key}/{id:long}")]
    public async Task<IActionResult> Get(string key, long id, CancellationToken ct)
    {
        var (e, pid, err) = await Resolve(key, ct); if (err is not null) return err;
        var row = await _crud.GetAsync(e!, id, pid, _ctx.UserId!.Value, ct);
        return row is null ? NotFound() : Ok(row);
    }

    [HttpPost("{key}")]
    public async Task<IActionResult> Create(string key, [FromBody] Dictionary<string, JsonElement> body, CancellationToken ct)
    {
        var (e, pid, err) = await Resolve(key, ct); if (err is not null) return err;
        if (!e!.Caps.Create) return StatusCode(StatusCodes.Status405MethodNotAllowed, new { error = $"{key} is not creatable here." });
        return await Guard(async () => new { id = await _crud.CreateAsync(e, body, pid, _ctx.UserId!.Value, ct) });
    }

    [HttpPut("{key}/{id:long}")]
    public async Task<IActionResult> Update(string key, long id, [FromBody] Dictionary<string, JsonElement> body, CancellationToken ct)
    {
        var (e, pid, err) = await Resolve(key, ct); if (err is not null) return err;
        if (!e!.Caps.Update) return StatusCode(StatusCodes.Status405MethodNotAllowed, new { error = $"{key} is not editable here." });
        return await Guard(async () => new { ok = await _crud.UpdateAsync(e, id, body, pid, _ctx.UserId!.Value, ct) });
    }

    [HttpDelete("{key}/{id:long}")]
    public async Task<IActionResult> Delete(string key, long id, CancellationToken ct)
    {
        var (e, pid, err) = await Resolve(key, ct); if (err is not null) return err;
        if (!e!.Caps.Delete) return StatusCode(StatusCodes.Status405MethodNotAllowed, new { error = $"{key} is not deletable here." });
        return await Guard(async () => new { ok = await _crud.DeleteAsync(e, id, pid, _ctx.UserId!.Value, ct) });
    }

    // ── shared: validate entity + resolve/authorize project ──
    private async Task<(EntityDescriptor? Entity, long ProjectId, IActionResult? Error)> Resolve(string key, CancellationToken ct)
    {
        var e = EntityRegistry.Find(key);
        if (e is null) return (null, 0, NotFound(new { error = $"unknown entity '{key}'." }));
        if (!_ctx.IsAuthenticated) return (null, 0, Unauthorized("Provide X-User-Id and X-Project-Slug."));
        var mine = await _directory.ListForUserAsync(_ctx.UserId!.Value, ct);
        var project = mine.FirstOrDefault(p => p.Slug == _ctx.ProjectSlug);
        if (project is null)
            return (null, 0, StatusCode(StatusCodes.Status403Forbidden, new { error = $"not a member of '{_ctx.ProjectSlug}'." }));
        return (e, project.Id, null);
    }

    private async Task<IActionResult> Guard<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (TenantWriteException ex) { return Conflict(new { error = ex.Message }); }
    }
}
