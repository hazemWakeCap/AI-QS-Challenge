using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>Grades the Proof-view backtest service against the real Tower X workbook.</summary>
public sealed class WatchlistBacktestServiceTests
{
    private static readonly IReadOnlyList<CostCentrePeriod> Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath);
    private static readonly TrainedModel Model = new RollingOriginEvaluator().Train(Panel);
    private static readonly WatchlistBacktestService Service = new(new WatchlistScoringService());

    [Fact]
    public void Period_5_reveals_bcc_str_con_204_as_the_top_flag_and_a_hit()
    {
        var r = Service.Evaluate(Panel, 5, k: 5, Model);
        Assert.NotNull(r);

        var top = r!.Rows[0];
        Assert.Equal("BCC-STR-CON-204", top.Row.BccId);   // #1 on the P5 watchlist
        Assert.Equal("AMBER", top.ActualNextAlert);        // actual P6 outcome
        Assert.True(top.Hit);                              // ⇒ HIT

        // 3/5 of the top-5 actually tipped (matches the hand-computed table).
        Assert.Equal(3, r.Hits);
        Assert.Equal(3.0 / 5.0, r.PrecisionAtK!.Value, 9);
    }

    [Fact]
    public void Hit_flag_is_exactly_amber_next_period_for_every_row_every_origin()
    {
        for (int p = 4; p <= 11; p++)
        {
            var r = Service.Evaluate(Panel, p, k: 5, Model);
            Assert.NotNull(r);
            foreach (var row in r!.Rows)
            {
                bool actualAmber = string.Equals(row.ActualNextAlert, "AMBER", StringComparison.OrdinalIgnoreCase);
                Assert.Equal(actualAmber, row.Hit); // Hit ⇔ actual next period is AMBER — no other definition
            }
            // precision@k ties out to hits / min(k, eligible).
            Assert.Equal((double)r.Hits / Math.Min(5, r.Eligible), r.PrecisionAtK!.Value, 9);
        }
    }

    [Fact]
    public void Forecast_period_is_not_backtestable()
    {
        // Period 12 is the live forecast — no successor to grade against.
        Assert.False(WatchlistBacktestService.IsBacktestable(Model.Origins, Model.Origins.ForecastPeriod));
        Assert.Null(Service.Evaluate(Panel, Model.Origins.ForecastPeriod, k: 5, Model));
    }
}
