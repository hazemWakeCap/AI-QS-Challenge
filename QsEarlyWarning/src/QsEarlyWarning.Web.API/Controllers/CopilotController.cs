using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Agent;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Contracts;
using QsEarlyWarning.Web.API.Tenancy;

namespace QsEarlyWarning.Web.API.Controllers;

[ApiController]
[Route("api/v1/copilot")]
public sealed class CopilotController : ControllerBase
{
    private const int MaxQuestionLength = 1000;
    private const int MaxHistory = 20;

    private readonly IQsCostCopilotAgent _agent;
    private readonly IProjectSnapshotRegistry _registry;
    private readonly ProjectDirectory _directory;
    private readonly WatchlistScoringService _scoring;
    private readonly TenantContext _ctx;

    public CopilotController(
        IQsCostCopilotAgent agent, IProjectSnapshotRegistry registry, ProjectDirectory directory,
        WatchlistScoringService scoring, TenantContext ctx)
    {
        _agent = agent;
        _registry = registry;
        _directory = directory;
        _scoring = scoring;
        _ctx = ctx;
    }

    /// <summary>
    /// Ask the QS Cost Copilot. Idea-4: the RLS-scoped project snapshot is resolved BEFORE the LLM runs
    /// (401/403/404), and the read-only tools are built per request from that snapshot — a non-member
    /// never reaches a tool call. Tools compute, the model narrates. 400 on malformed input.
    /// </summary>
    [HttpPost("ask")]
    public async Task<ActionResult<CopilotAskResponse>> Ask(
        [FromBody] CopilotAskRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Question))
            return BadRequest("question is required.");
        if (request.Question.Length > MaxQuestionLength)
            return BadRequest($"question exceeds {MaxQuestionLength} characters.");
        if (request.History is { Count: > MaxHistory })
            return BadRequest($"history exceeds {MaxHistory} turns.");

        // Resolve the tenant snapshot first (RLS boundary before any LLM/tool call).
        if (!_ctx.IsAuthenticated) return Unauthorized("Provide X-User-Id and X-Project-Slug.");
        var mine = await _directory.ListForUserAsync(_ctx.UserId!.Value, ct);
        var project = mine.FirstOrDefault(p => p.Slug == _ctx.ProjectSlug);
        if (project is null)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = $"not a member of '{_ctx.ProjectSlug}'." });

        ProjectSnapshot snapshot;
        try { snapshot = await _registry.GetOrBuildAsync(project.Id, _ctx.UserId.Value, ct); }
        catch (InvalidOperationException) { return StatusCode(StatusCodes.Status404NotFound, new { error = "no data for this project yet." }); }

        var tools = new QsAnalyticsTools(snapshot, _scoring);
        var history = (request.History ?? Array.Empty<CopilotTurnDto>())
            .Select(t => new CopilotTurn(t.Role, t.Text))
            .ToList();

        var result = await _agent.AskAsync(request.Question, history, tools, ct);

        return Ok(new CopilotAskResponse(
            result.Answer,
            result.Refused,
            result.Evidence.Select(e => new CopilotEvidenceDto(e.Tool, e.Detail,
                e.Sources is null ? null
                    : new CopilotSourcesDto(e.Sources.Sheet, e.Sources.ResolvedPeriod, e.Sources.Filter,
                        e.Sources.ExcludedCount, e.Sources.RowIds))).ToList()));
    }
}
