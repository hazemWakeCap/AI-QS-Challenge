using QsEarlyWarning.Core.Agent;

namespace QsEarlyWarning.Agent;

/// <summary>
/// Registered when no Anthropic API key is configured. Keeps the copilot endpoint alive with a
/// clear message; the watchlist and validation views are unaffected (plan §6.9).
/// </summary>
public sealed class DisabledCopilotAgent : IQsCostCopilotAgent
{
    public Task<CopilotAskResult> AskAsync(
        string question, IReadOnlyList<CopilotTurn> history, CancellationToken ct)
        => Task.FromResult(CopilotAskResult.Text(
            "The copilot is not configured. Set the ANTHROPIC_API_KEY environment variable (or " +
            "Copilot:AnthropicApiKey in config) and restart the API to enable it. The watchlist and " +
            "model-validation views work without it.",
            refused: true));
}
