using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Fixture/snapshot tier — asserts facts about THIS supplied workbook (plan §6.11).
/// These live in tests, not the loader, so a legitimate workbook refresh does not break them.
/// </summary>
public sealed class DataContractTests
{
    private static readonly IReadOnlyList<Domain.Entities.CostCentrePeriod> Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath);

    [Fact]
    public void EpFilter_yields_2076_rows_and_173_centres()
    {
        Assert.Equal(2076, Panel.Count);
        Assert.Equal(173, Panel.Select(p => p.BccId).Distinct().Count());
    }

    [Fact]
    public void Only_EP_packages_survive()
    {
        Assert.All(Panel, p => Assert.StartsWith("EP-", p.PackageCode));
    }

    [Fact]
    public void Amber_iff_cpi_below_095_on_live_green_amber_rows()
    {
        var live = Panel.Where(p => p.AlertLevel is "GREEN" or "AMBER" && p.Cpi is not null).ToList();
        Assert.Equal(1163, live.Count);

        var violations = live.Count(p => (p.AlertLevel == "AMBER") != (p.Cpi!.Value < 0.95));
        Assert.Equal(0, violations);
    }

    [Fact]
    public void Alert_levels_are_within_the_permitted_set()
    {
        var allowed = new HashSet<string?> { "GREEN", "AMBER", "CLOSED", "NOT STARTED", null };
        Assert.All(Panel, p => Assert.Contains(p.AlertLevel, allowed));
    }
}
