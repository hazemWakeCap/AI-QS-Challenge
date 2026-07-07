using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core.Agent;
using QsEarlyWarning.Web.API.Contracts;

namespace QsEarlyWarning.Web.API.Controllers;

[ApiController]
[Route("api/v1/copilot")]
public sealed class CopilotController : ControllerBase
{
    private const int MaxQuestionLength = 1000;
    private const int MaxHistory = 20;

    private readonly IQsCostCopilotAgent _agent;

    public CopilotController(IQsCostCopilotAgent agent) => _agent = agent;

    /// <summary>
    /// Ask the QS Cost Copilot. Grounded in read-only tools over the same scoring path as the
    /// watchlist. 400 on malformed input. Plan §6.8/§6.9.
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

        var history = (request.History ?? Array.Empty<CopilotTurnDto>())
            .Select(t => new CopilotTurn(t.Role, t.Text))
            .ToList();

        var result = await _agent.AskAsync(request.Question, history, ct);

        return Ok(new CopilotAskResponse(
            result.Answer,
            result.Refused,
            result.Evidence.Select(e => new CopilotEvidenceDto(e.Tool, e.Detail)).ToList()));
    }
}
