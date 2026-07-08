using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QsEarlyWarning.Core.Agent;

namespace QsEarlyWarning.Agent;

public static class AgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers the QS Cost Copilot. If an Anthropic API key is present (Copilot:AnthropicApiKey
    /// or ANTHROPIC_API_KEY), wires the Microsoft Agent Framework agent over Claude; otherwise
    /// registers a disabled agent so the endpoint stays alive with a clear message. Plan §6.8/§6.9.
    /// Secrets come from env/user-secrets, never appsettings.
    /// </summary>
    public static IServiceCollection AddQsCostCopilot(this IServiceCollection services, IConfiguration config)
    {
        // Idea-4: QsAnalyticsTools are now built PER REQUEST from the caller's tenant-scoped snapshot
        // (in CopilotController, after RLS membership resolution) — no longer a singleton.
        var apiKey = config["Copilot:AnthropicApiKey"]
                     ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var model = config["Copilot:Model"] ?? "claude-sonnet-5";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            services.AddSingleton<IQsCostCopilotAgent, DisabledCopilotAgent>();
            return services;
        }

        // Anthropic-backed IChatClient (Microsoft.Extensions.AI), reused by the agent.
        services.AddSingleton<IChatClient>(_ =>
            new AnthropicClient { ApiKey = apiKey }
                .AsIChatClient(model, defaultMaxOutputTokens: 1024)
                .AsBuilder()
                .Build());

        services.AddSingleton<IQsCostCopilotAgent>(sp => new ClaudeQsCostCopilotAgent(
            sp.GetRequiredService<IChatClient>(),
            sp.GetRequiredService<ILogger<ClaudeQsCostCopilotAgent>>()));

        return services;
    }
}
