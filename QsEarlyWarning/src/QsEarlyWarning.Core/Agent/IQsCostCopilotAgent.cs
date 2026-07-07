namespace QsEarlyWarning.Core.Agent;

/// <summary>A conversation turn — a plain Core DTO, NOT a framework type (plan §6.8).</summary>
public sealed record CopilotTurn(string Role, string Text);

/// <summary>A sanitized citation: what the agent looked at, not raw framework tool-call objects.</summary>
public sealed record CopilotEvidence(string Tool, string Detail);

public sealed record CopilotAskResult
{
    public required string Answer { get; init; }
    public required IReadOnlyList<CopilotEvidence> Evidence { get; init; }
    public bool Refused { get; init; }

    public static CopilotAskResult Text(string answer, bool refused = false) =>
        new() { Answer = answer, Evidence = Array.Empty<CopilotEvidence>(), Refused = refused };
}

/// <summary>
/// The QS Cost Copilot contract, owned by Core so the model impl is swappable (plan §6.8).
/// Takes CopilotTurn (not the framework's AgentMessage) so Core has zero MAF dependency.
/// </summary>
public interface IQsCostCopilotAgent
{
    Task<CopilotAskResult> AskAsync(string question, IReadOnlyList<CopilotTurn> history, CancellationToken ct);
}
