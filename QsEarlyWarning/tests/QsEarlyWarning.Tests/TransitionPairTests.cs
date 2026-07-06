using QsEarlyWarning.Core.Features;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

public sealed class TransitionPairTests
{
    private static readonly IReadOnlyList<CostCentrePeriod> Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath);

    private static readonly FeatureBuilder Builder = new();

    [Fact]
    public void Real_workbook_has_117_green_to_amber_transitions()
    {
        var result = Builder.BuildPairs(Panel, 1, 11);
        // The headline: exactly 117 GREEN→AMBER transitions (matches the plan / data probe).
        Assert.Equal(117, result.Pairs.Count(p => p.Label));
        // Paired GREEN-at-p population with an exact live successor = 670 (117 AMBER + 3 CLOSED + 550 GREEN).
        Assert.Equal(670, result.Pairs.Count);
        // The lone GREEN(10)→NOT STARTED(11) plus any GREEN-at-p with no exact successor are excluded, counted.
        Assert.True(result.ExcludedCount >= 1);
    }

    [Fact]
    public void Green_to_closed_is_a_negative_not_a_drop()
    {
        var panel = new[]
        {
            Row("B1", 1, "GREEN", cpi: 0.98, pbc: 30, apc: 28),
            Row("B1", 2, "CLOSED", cpi: 0.98, pbc: 30, apc: 28),
        };
        var pairs = Builder.BuildPairsForPeriod(panel, 1).Pairs;
        Assert.Single(pairs);
        Assert.False(pairs[0].Label); // CLOSED successor → negative, kept
    }

    [Fact]
    public void Green_to_not_started_is_excluded_and_counted()
    {
        var panel = new[]
        {
            Row("B1", 1, "GREEN", cpi: 0.98, pbc: 30, apc: 28),
            Row("B1", 2, "NOT STARTED", cpi: null, pbc: null, apc: null),
        };
        var result = Builder.BuildPairsForPeriod(panel, 1);
        Assert.Empty(result.Pairs);
        Assert.Equal(1, result.ExcludedCount);
    }

    [Fact]
    public void No_false_adjacency_across_a_missing_period()
    {
        // p=1 GREEN, then p=3 AMBER (p=2 missing). Pairing p=1 must NOT reach p=3.
        var panel = new[]
        {
            Row("B1", 1, "GREEN", cpi: 0.98, pbc: 30, apc: 28),
            Row("B1", 3, "AMBER", cpi: 0.93, pbc: 40, apc: 37),
        };
        var result = Builder.BuildPairsForPeriod(panel, 1);
        Assert.Empty(result.Pairs);          // no successor at exactly p=2
        Assert.Equal(1, result.ExcludedCount);
    }

    [Fact]
    public void Lag_delta_requires_exact_predecessor_else_missing()
    {
        // p-1 missing → dCpi1 null; but p-? present chain differs.
        var panel = new[]
        {
            Row("B1", 1, "GREEN", cpi: 0.99, pbc: 20, apc: 19),
            // period 2 missing
            Row("B1", 3, "GREEN", cpi: 0.97, pbc: 30, apc: 27),
            Row("B1", 4, "AMBER", cpi: 0.94, pbc: 40, apc: 37),
        };
        var pair = Builder.BuildPairsForPeriod(panel, 3).Pairs.Single();
        Assert.Null(pair.DCpi1); // predecessor period 2 is absent → no delta across the gap
        Assert.True(pair.Label);
    }

    [Fact]
    public void Lag_delta_present_with_exact_consecutive_predecessor()
    {
        var panel = new[]
        {
            Row("B1", 1, "GREEN", cpi: 0.99, pbc: 20, apc: 19),
            Row("B1", 2, "GREEN", cpi: 0.97, pbc: 30, apc: 27),
            Row("B1", 3, "AMBER", cpi: 0.94, pbc: 40, apc: 37),
        };
        var pair = Builder.BuildPairsForPeriod(panel, 2).Pairs.Single();
        Assert.NotNull(pair.DCpi1);
        Assert.Equal(0.97 - 0.99, pair.DCpi1!.Value, 6);
        Assert.Equal((30 - 27) - (20 - 19), pair.DGap1!.Value, 6);
    }

    private static CostCentrePeriod Row(
        string bcc, int period, string? alert, double? cpi, double? pbc, double? apc)
        => new()
        {
            BccId = bcc, PeriodId = period, PackageCode = "EP-CIV-DEMO",
            Discipline = "Civil", AlertLevel = alert,
            Cpi = cpi, PctBudgetConsumed = pbc, ActualPctComplete = apc,
            AcCumulative = 1000,
        };
}
