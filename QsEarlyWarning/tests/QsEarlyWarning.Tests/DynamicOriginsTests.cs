using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Proves the analytics are period-dynamic (plan §5b, codex Finding 1): origins are derived from the
/// data, not the compile-time 4/11/12 constants. Tower X is unchanged; a shifted panel moves with it.
/// </summary>
public sealed class DynamicOriginsTests
{
    private static readonly CostCentrePeriod[] Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath).ToArray();

    [Fact]
    public void Default_origins_match_tower_x_periods_1_to_12()
    {
        var m = new RollingOriginEvaluator().Train(Panel);
        Assert.Equal(4, m.Origins.FirstOrigin);
        Assert.Equal(11, m.Origins.LastLabeledPeriod);
        Assert.Equal(12, m.Origins.ForecastPeriod);
        Assert.Equal(ArtifactRole.Forecast, m.ArtifactFor(12)!.Role);
    }

    [Fact]
    public void Origins_follow_the_data_when_periods_are_shifted()
    {
        // Same shape, every reporting period shifted +4 → periods 5..16.
        var shifted = Panel.Select(p => p with { PeriodId = p.PeriodId + 4 }).ToArray();
        var m = new RollingOriginEvaluator().Train(shifted);

        Assert.Equal(8, m.Origins.FirstOrigin);          // min(5) + lead(3)
        Assert.Equal(15, m.Origins.LastLabeledPeriod);   // latest with a successor
        Assert.Equal(16, m.Origins.ForecastPeriod);      // latest present period — NOT the constant 12

        Assert.Equal(ArtifactRole.Forecast, m.ArtifactFor(16)!.Role);
        Assert.Equal(ArtifactRole.Oof, m.ArtifactFor(8)!.Role);   // first scored origin
        Assert.Null(m.ArtifactFor(7));                            // before first origin
        Assert.Null(m.ArtifactFor(4));                            // 12-era constant no longer special
    }
}
