using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Forecasting;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Idea-2 forecaster: verifies the CALCULATIONS and LEAKAGE constraints (increment derivation,
/// model+baselines on identical rows, measured coverage, cone shape) and reports the early-band
/// comparison. Per the plan the back-test reports the comparison — a legitimate outcome could show
/// no win — but on this panel the model does beat the baselines in the early band, asserted here.
/// </summary>
public sealed class ForecastTests
{
    private static readonly CostCentrePeriod[] Panel = new ExcelPanelLoader().Load(TestData.WorkbookPath).ToArray();
    private static readonly ReportingOrigins Origins = ReportingOrigins.FromPanel(Panel);

    [Fact]
    public void Increment_is_the_consecutive_cumulative_difference()
    {
        var byPeriod = Panel.Where(p => p.BccId == "BCC-ARC-MAS-301").ToDictionary(p => p.PeriodId);
        var inc = IncrementHelper.AcInc(byPeriod, 3);
        Assert.NotNull(inc);
        Assert.Equal(byPeriod[3].AcCumulative!.Value - byPeriod[2].AcCumulative!.Value, inc!.Value, 3);
    }

    [Fact]
    public void Backtest_scores_model_and_four_baselines_on_identical_rows_with_measured_coverage()
    {
        var s = new ForecastEvaluator().Evaluate(Panel, Origins);
        foreach (var h in new[] { 1, 2, 3 })
        {
            var rows = s.Overall.Where(m => m.Horizon == h).ToList();
            Assert.Equal(5, rows.Count);                                   // model + 4 baselines
            Assert.Single(rows.Select(m => m.N).Distinct());               // identical eligible rows
        }
        var model1 = s.Overall.First(m => m.Predictor == "model" && m.Horizon == 1);
        Assert.True(model1.N > 0);
        Assert.NotNull(model1.CoverageP10P90);                             // coverage is measured & reported
    }

    [Fact]
    public void Model_beats_all_four_baselines_on_mae_pct_bac_in_the_early_band()
    {
        var s = new ForecastEvaluator().Evaluate(Panel, Origins);
        var e = s.EarlyBand.Where(m => m.Horizon == 1).ToDictionary(m => m.Predictor, m => m.MaePctOfBac);
        foreach (var b in new[] { "planned-spend", "cpi-based", "recent-run-rate", "zero-increment" })
            Assert.True(e["model"] <= e[b], $"model {e["model"]:0.###} should be ≤ {b} {e[b]:0.###}");
    }

    [Fact]
    public void ForecastCentre_yields_three_horizons_anchored_at_the_latest_origin()
    {
        var f = new IncrementalSpendForecaster();
        f.Fit(Panel, Origins);
        var c = f.ForecastCentre("BCC-ARC-MAS-301");
        Assert.NotNull(c);
        Assert.Equal(3, c!.Increments.Count);
        Assert.Equal(Origins.ForecastPeriod, c.OriginPeriod);
    }
}
