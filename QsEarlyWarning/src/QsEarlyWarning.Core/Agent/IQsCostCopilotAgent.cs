namespace QsEarlyWarning.Core.Agent;

/// <summary>A conversation turn — a plain Core DTO, NOT a framework type (plan §6.8).</summary>
public sealed record CopilotTurn(string Role, string Text);

/// <summary>Structured provenance for one tool call (idea-4 G7): the sheet, resolved period, resolved
/// filter, excluded-row count, and the source row IDs (natural keys — "{BccId}@P{PeriodId}" for panel
/// rows, BOQ item refs for estimate rows). Tools return this as a `sources` field on their result;
/// aggregation tools that carry per-ratio counts leave <see cref="ExcludedCount"/> null.</summary>
public sealed record CopilotSources(
    string? Sheet, int? ResolvedPeriod, string? Filter, int? ExcludedCount, IReadOnlyList<string> RowIds);

/// <summary>A sanitized citation: what the agent looked at, not raw framework tool-call objects.</summary>
public sealed record CopilotEvidence(string Tool, string Detail, CopilotSources? Sources = null);

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
    /// <summary>Answer a question using the supplied per-request tools (bound to the caller's resolved,
    /// RLS-scoped project snapshot). Idea-4: tools are built per request from the tenant snapshot.</summary>
    Task<CopilotAskResult> AskAsync(
        string question, IReadOnlyList<CopilotTurn> history, QsAnalyticsTools tools, CancellationToken ct);
}
