using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using QsEarlyWarning.Agent.Prompts;
using QsEarlyWarning.Core.Agent;

namespace QsEarlyWarning.Agent;

/// <summary>
/// Microsoft Agent Framework copilot backed by Anthropic Claude (plan §6.8), cloning the WakeCap
/// ClaudeTrainingCenterAgent pattern: a ChatClientAgent over read-only QsAnalyticsTools, with
/// tool-call tracking middleware for sanitized evidence, a pre-flight scope-rejection guard, and
/// graceful tool-error handling. Implements the Core-owned IQsCostCopilotAgent so the model is
/// swappable (Core carries no MAF dependency).
/// </summary>
public sealed class ClaudeQsCostCopilotAgent : IQsCostCopilotAgent
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<ClaudeQsCostCopilotAgent> _logger;

    private const int MaxHistoryTurns = 10;

    // Pre-flight rejection for plainly off-topic asks (defence-in-depth; real enforcement is the
    // read-only tool surface + per-tool arg validation).
    private static readonly Regex OutOfScope = new(
        @"\b(weather|joke|funny|riddle|recipe|cook(?:ing)?|poem|song|lyrics|story|stock price|" +
        @"exchange rate|sports|movie|netflix|spotify|who won|president|capital of)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public ClaudeQsCostCopilotAgent(
        IChatClient chatClient, ILogger<ClaudeQsCostCopilotAgent> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
    }

    public async Task<CopilotAskResult> AskAsync(
        string question, IReadOnlyList<CopilotTurn> history, QsAnalyticsTools tools, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(question))
            return CopilotAskResult.Text("Ask me about a cost centre or the watchlist.", refused: true);

        if (OutOfScope.IsMatch(question))
            return CopilotAskResult.Text(CopilotPrompts.OutOfScopeRefusal, refused: true);

        var tracker = new ToolCallTracker();
        var agent = BuildAgent(tracker, tools);

        var messages = BuildMessages(question, history);
        // Note: newer Claude models reject the `temperature` parameter, so we don't set it.
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions());

        try
        {
            var response = await agent.RunAsync(messages, session: null, options: runOptions, ct)
                .ConfigureAwait(false);

            var answer = response.Text ?? string.Empty;
            return new CopilotAskResult
            {
                Answer = string.IsNullOrWhiteSpace(answer)
                    ? "I couldn't find anything to report for that."
                    : answer,
                Evidence = tracker.Evidence,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot run failed.");
            return CopilotAskResult.Text(
                "The copilot is temporarily unavailable. The watchlist and validation views are unaffected.",
                refused: true);
        }
    }

    private AIAgent BuildAgent(ToolCallTracker tracker, QsAnalyticsTools tools)
    {
        var aiTools = new List<AITool>
        {
            AIFunctionFactory.Create(tools.GetWatchlist),
            AIFunctionFactory.Create(tools.GetCostCentreDetail),
            AIFunctionFactory.Create(tools.ExplainDrift),
            AIFunctionFactory.Create(tools.GetEvmSnapshot),
            AIFunctionFactory.Create(tools.ListCentresByProgress),
            AIFunctionFactory.Create(tools.ForecastIncrementalSpend),
            AIFunctionFactory.Create(tools.ScenarioForecast),
            AIFunctionFactory.Create(tools.DirectionalEac),
            AIFunctionFactory.Create(tools.ResourceSplit),
            AIFunctionFactory.Create(tools.ProjectEvm),
            AIFunctionFactory.Create(tools.StressFlagsForPackage),
            AIFunctionFactory.Create(tools.ExplainVariance),
            AIFunctionFactory.Create(tools.LocateCostRisk),
        };

        // Middleware: record each tool call (args + returned `sources` provenance) for the sanitized
        // evidence trail, and turn tool exceptions into a structured result the model can recover from
        // (never 500 the run).
        async ValueTask<object?> ToolMiddleware(
            AIAgent agent,
            FunctionInvocationContext ctx,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
            CancellationToken ct)
        {
            var result = await InvokeSafely(ctx, next, ct).ConfigureAwait(false);
            tracker.Record(ctx.Function.Name, ctx.Arguments, ExtractSources(result));
            return result;
        }

        return new ChatClientAgent(
                chatClient: _chatClient,
                instructions: CopilotPrompts.System,
                name: "QsCostCopilot",
                description: "Read-only QS cost / EVM analytics agent.",
                tools: aiTools)
            .AsBuilder()
            .Use((Func<AIAgent, FunctionInvocationContext,
                Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>,
                CancellationToken, ValueTask<object?>>)ToolMiddleware)
            .Build();
    }

    private static async ValueTask<object?> InvokeSafely(
        FunctionInvocationContext ctx,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken ct)
    {
        try
        {
            return await next(ctx, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new { error = $"tool '{ctx.Function.Name}' failed: {ex.Message}" };
        }
    }

    private static List<ChatMessage> BuildMessages(string question, IReadOnlyList<CopilotTurn> history)
    {
        var messages = new List<ChatMessage>();
        foreach (var turn in history.TakeLast(MaxHistoryTurns))
        {
            var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? ChatRole.Assistant
                : ChatRole.User;
            if (!string.IsNullOrWhiteSpace(turn.Text))
                messages.Add(new ChatMessage(role, turn.Text));
        }
        messages.Add(new ChatMessage(ChatRole.User, question));
        return messages;
    }

    /// <summary>Reflects the tool result's `sources` property (a Core <see cref="CopilotSources"/>)
    /// so the evidence trail carries the sheet / resolved filter / excluded count / source row IDs.</summary>
    private static CopilotSources? ExtractSources(object? result) =>
        result?.GetType().GetProperty("sources")?.GetValue(result) as CopilotSources;

    /// <summary>Records tool calls into a sanitized evidence trail (no raw framework objects).</summary>
    private sealed class ToolCallTracker
    {
        private readonly List<CopilotEvidence> _evidence = new();
        public IReadOnlyList<CopilotEvidence> Evidence => _evidence;

        public void Record(string tool, IDictionary<string, object?>? args, CopilotSources? sources)
        {
            var detail = args is null || args.Count == 0
                ? ""
                : string.Join(", ", args.Select(kv => $"{kv.Key}={kv.Value}"));
            _evidence.Add(new CopilotEvidence(tool, detail, sources));
        }
    }
}
