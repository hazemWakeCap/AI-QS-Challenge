using QsEarlyWarning.Core;
using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;
using Xunit.Abstractions;

namespace QsEarlyWarning.Tests;

public sealed class ArtifactLifecycleAndEvaluationTests
{
    private readonly ITestOutputHelper _out;
    public ArtifactLifecycleAndEvaluationTests(ITestOutputHelper output) => _out = output;

    private static readonly Domain.Entities.CostCentrePeriod[] Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath).ToArray();

    private static readonly TrainedModel Model = new RollingOriginEvaluator().Train(Panel);

    [Fact]
    public void Oof_artifacts_exist_for_origins_4_through_11()
    {
        for (int o = EvmThresholds.MinTrainOrigin; o <= EvmThresholds.LastLabeledPeriod; o++)
        {
            var a = Model.ArtifactFor(o);
            Assert.NotNull(a);
            Assert.Equal(ArtifactRole.Oof, a!.Role);
            Assert.Equal(o, a.TrainingCutoffPeriod);
        }
    }

    [Fact]
    public void Forecast_artifact_has_cutoff_12_and_forecast_role()
    {
        var f = Model.ArtifactFor(EvmThresholds.ForecastPeriod);
        Assert.NotNull(f);
        Assert.Equal(ArtifactRole.Forecast, f!.Role);
        Assert.Equal(12, f.TrainingCutoffPeriod);
    }

    [Fact]
    public void No_artifact_before_min_origin()
        => Assert.Null(Model.ArtifactFor(EvmThresholds.MinTrainOrigin - 1));

    [Fact]
    public void Scoring_is_deterministic_for_same_input_and_artifact()
    {
        var svc = new WatchlistScoringService();
        var a = svc.ScorePeriod(Panel, 8, Model);
        var b = svc.ScorePeriod(Panel, 8, Model);
        Assert.Equal(a.Rows.Select(r => (r.BccId, r.RiskScore)),
                     b.Rows.Select(r => (r.BccId, r.RiskScore)));
    }

    [Fact]
    public void Forecast_period_12_scores_its_green_population_despite_no_successor()
    {
        var svc = new WatchlistScoringService();
        var r = svc.ScorePeriod(Panel, EvmThresholds.ForecastPeriod, Model);
        Assert.Equal(ScoreStatus.Ok, r.Status);
        Assert.True(r.IsForecast);
        Assert.Equal(113, r.Rows.Count); // 113 GREEN centres at period 12 (verified against the workbook)
        // Ranked descending by risk score.
        Assert.True(r.Rows.Zip(r.Rows.Skip(1)).All(p => p.First.RiskScore >= p.Second.RiskScore));
    }

    [Fact]
    public void Well_formed_period_without_artifact_returns_not_found()
    {
        var svc = new WatchlistScoringService();
        var r = svc.ScorePeriod(Panel, 1, Model); // period 1 < MinTrainOrigin → no artifact
        Assert.Equal(ScoreStatus.NoArtifact, r.Status);
    }

    [Fact]
    public void Evaluation_produces_eight_folds_and_reports_the_rule_vs_cpi_native()
    {
        var s = Model.Summary;
        Assert.Equal(8, s.FoldCount);                     // origins 4..11
        Assert.Equal(117, s.TotalTransitions);

        var rule5 = s.Rule.Single(r => r.K == 5);
        var cpi5 = s.CpiNative.Where(r => r.K == 5).ToList();

        _out.WriteLine($"Rule    precision@5 macro = {rule5.MacroPrecision:0.000} " +
                       $"range [{rule5.PrecisionMin:0.000},{rule5.PrecisionMax:0.000}] " +
                       $"FP/cycle = {rule5.FalseAlertsPerCycle:0.00}");
        foreach (var c in cpi5)
            _out.WriteLine($"{c.ScorerLabel} precision@5 macro = {c.MacroPrecision:0.000}");

        Assert.NotNull(rule5.MacroPrecision);
        // Sanity: the deployed rule should at least reach the best CPI-native comparator.
        var bestCpi = cpi5.Max(c => c.MacroPrecision ?? 0);
        Assert.True(rule5.MacroPrecision >= bestCpi - 1e-9,
            $"rule {rule5.MacroPrecision:0.000} < best cpi-native {bestCpi:0.000}");
    }
}
