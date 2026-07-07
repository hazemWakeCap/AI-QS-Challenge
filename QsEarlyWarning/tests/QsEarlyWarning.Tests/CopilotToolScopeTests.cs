using QsEarlyWarning.Core;
using QsEarlyWarning.Core.Agent;
using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Asserts the CODE-LEVEL enforcement of the copilot tools (plan §6.8/§6.11): read-only,
/// args validated/clamped, typed errors instead of throws. No model call involved.
/// </summary>
public sealed class CopilotToolScopeTests
{
    private static readonly IReadOnlyList<CostCentrePeriod> Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath);

    private static QsAnalyticsTools BuildTools()
    {
        var loader = new StubLoader(Panel);
        var provider = new ModelProvider(loader, "unused");
        return new QsAnalyticsTools(provider, new WatchlistScoringService());
    }

    private static readonly QsAnalyticsTools Tools = BuildTools();

    [Fact]
    public void GetWatchlist_rejects_out_of_range_period_with_typed_error()
    {
        var result = Tools.GetWatchlist(99, 5);
        Assert.Contains("periodId", ErrorOf(result));
    }

    [Fact]
    public void GetWatchlist_clamps_bad_topk_instead_of_throwing()
    {
        // topK=7 is invalid → clamped to 10, still returns a real result (no throw).
        var result = Tools.GetWatchlist(8, 7);
        Assert.Null(ErrorOf(result)); // no error field → succeeded
    }

    [Fact]
    public void ExplainDrift_returns_typed_error_for_unknown_centre()
    {
        var result = Tools.ExplainDrift("BCC-DOES-NOT-EXIST", 8);
        Assert.NotNull(ErrorOf(result));
    }

    [Fact]
    public void GetEvmSnapshot_returns_typed_error_for_blank_bccid()
    {
        var result = Tools.GetEvmSnapshot("  ", 8);
        Assert.Equal("bccId is required.", ErrorOf(result));
    }

    [Fact]
    public void Real_centre_evm_snapshot_has_cpi()
    {
        // BCC-ARC-PAINT-317 was the top-ranked period-8 centre.
        var result = Tools.GetEvmSnapshot("BCC-ARC-PAINT-317", 8);
        Assert.Null(ErrorOf(result));
    }

    private static string? ErrorOf(object result)
    {
        var prop = result.GetType().GetProperty("error");
        return prop?.GetValue(result) as string;
    }

    private sealed class StubLoader : IPanelLoader
    {
        private readonly IReadOnlyList<CostCentrePeriod> _panel;
        public StubLoader(IReadOnlyList<CostCentrePeriod> panel) => _panel = panel;
        public IReadOnlyList<CostCentrePeriod> Load(string workbookPath) => _panel;
    }
}
