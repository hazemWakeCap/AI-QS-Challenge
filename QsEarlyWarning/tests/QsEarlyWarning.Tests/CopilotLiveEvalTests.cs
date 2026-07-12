using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using QsEarlyWarning.Agent;
using QsEarlyWarning.Core.Agent;
using QsEarlyWarning.Core.Scoring;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Idea-4 OPT-IN live-LLM eval — runs the real Claude tool-use loop against the fixed questions. Gated on
/// ANTHROPIC_API_KEY: when absent (e.g. CI) it soft-returns, so it is NEVER a CI regression gate. When a
/// key is present it checks that the model routes each question to the RIGHT tool and produces an answer
/// with a source trail — the "vs manual lookup / time-to-answer" demo story. The deterministic numeric
/// correctness lives in <see cref="CopilotEvalTests"/> (that is the credibility artifact).
/// </summary>
public sealed class CopilotLiveEvalTests
{
    private static string? Key =>
        Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
        ?? Environment.GetEnvironmentVariable("Copilot__AnthropicApiKey");

    private static (ClaudeQsCostCopilotAgent Agent, QsAnalyticsTools Tools)? BuildOrSkip()
    {
        if (string.IsNullOrWhiteSpace(Key)) return null;   // opt-in: no key → soft skip
        var model = Environment.GetEnvironmentVariable("Copilot__Model") ?? "claude-sonnet-5";
        var chat = new AnthropicClient { ApiKey = Key }.AsIChatClient(model, defaultMaxOutputTokens: 1024)
            .AsBuilder().Build();
        var agent = new ClaudeQsCostCopilotAgent(chat, NullLogger<ClaudeQsCostCopilotAgent>.Instance);
        var tools = new QsAnalyticsTools(TestSnapshot.Build(), new WatchlistScoringService());
        return (agent, tools);
    }

    [Fact]
    public async Task Watchlist_question_routes_to_the_watchlist_tool()
    {
        if (BuildOrSkip() is not var (agent, tools) || agent is null) return;
        var res = await agent.AskAsync(
            "Which centres are about to tip to AMBER in period 12, and why?",
            Array.Empty<CopilotTurn>(), tools, default);
        Assert.False(string.IsNullOrWhiteSpace(res.Answer));
        Assert.Contains(res.Evidence, e => e.Tool == nameof(QsAnalyticsTools.GetWatchlist));
    }

    [Fact]
    public async Task Project_cpi_question_routes_to_project_evm_not_a_hand_computed_number()
    {
        if (BuildOrSkip() is not var (agent, tools) || agent is null) return;
        var res = await agent.AskAsync(
            "What is the project CPI in period 8?",
            Array.Empty<CopilotTurn>(), tools, default);
        Assert.Contains(res.Evidence, e => e.Tool == nameof(QsAnalyticsTools.ProjectEvm));
    }

    [Fact]
    public async Task Unit_rate_whatif_routes_to_the_scenario_tool()
    {
        if (BuildOrSkip() is not var (agent, tools) || agent is null) return;
        var res = await agent.AskAsync(
            "Assume we renegotiate BCC-MEC-DUCT-702 to 299 per unit from next period — forecast the next 3 periods.",
            Array.Empty<CopilotTurn>(), tools, default);
        Assert.False(string.IsNullOrWhiteSpace(res.Answer));
        Assert.Contains(res.Evidence, e => e.Tool == nameof(QsAnalyticsTools.ScenarioForecast));
    }

    [Fact]
    public async Task Final_cost_question_uses_the_directional_tool()
    {
        if (BuildOrSkip() is not var (agent, tools) || agent is null) return;
        var res = await agent.AskAsync(
            "What's the forecast final cost for the top drifting centre?",
            Array.Empty<CopilotTurn>(), tools, default);
        // Either it uses directional_eac (flagged) or the validated incremental-spend forecast — never
        // a fabricated final cost. Assert it called at least one forecast/EAC tool.
        Assert.Contains(res.Evidence, e =>
            e.Tool == nameof(QsAnalyticsTools.DirectionalEac)
            || e.Tool == nameof(QsAnalyticsTools.ForecastIncrementalSpend));
    }
}
