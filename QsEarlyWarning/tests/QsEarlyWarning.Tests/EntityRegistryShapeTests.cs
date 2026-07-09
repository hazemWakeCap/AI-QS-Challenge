using QsEarlyWarning.Infrastructure.Crud;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Guards the workbook grouping/lineage metadata that drives the sheet-first Data-Admin nav
/// (plan: "Enhance Data Admin UX"). Nav order must come from GroupOrder+Order (never array
/// position), groups must match the canonical mapping, and lineage (SheetRef/Blurb) must stay
/// provenance-accurate — the sheet-9 source tables ARE imported, only the computed EVM is derived,
/// and cost-deltas is live-capture (not sheet-9), even though it lives in the periods group.
/// </summary>
public sealed class EntityRegistryShapeTests
{
    private static readonly IReadOnlyList<EntityDescriptor> All = EntityRegistry.All;

    // The canonical group sequence, ordered by GroupOrder (BOQ first, System last).
    private static readonly string[] ExpectedGroupOrder =
        { "boq", "norms", "mapping", "datasheet", "cost-centres", "periods", "system" };

    // Sheet-9 SOURCE inputs that WorkbookImporter loads directly from 9_HISTORICAL_DATA.
    private static readonly string[] ImportedSheet9 =
        { "reporting-periods", "cost-centre-periods", "cost-centres", "baselines", "plan-periods" };

    [Fact] // (a) every entity carries non-empty group nav metadata
    public void Every_entity_has_group_metadata()
    {
        Assert.Equal(14, All.Count);
        Assert.All(All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Group), $"{e.Key} has no Group");
            Assert.False(string.IsNullOrWhiteSpace(e.GroupLabel), $"{e.Key} has no GroupLabel");
            Assert.False(string.IsNullOrWhiteSpace(e.Blurb), $"{e.Key} has no Blurb");
        });
    }

    [Fact] // (b) exactly 7 distinct groups
    public void There_are_exactly_seven_groups()
    {
        Assert.Equal(7, All.Select(e => e.Group).Distinct().Count());
    }

    [Fact] // (c) group order (by GroupOrder) matches the canonical mapping
    public void Group_order_matches_the_mapping()
    {
        var ordered = All
            .GroupBy(e => e.Group)
            .OrderBy(g => g.First().GroupOrder)
            .Select(g => g.Key)
            .ToArray();
        Assert.Equal(ExpectedGroupOrder, ordered);
    }

    [Fact] // total ordering: (GroupOrder, Order) has no duplicate within a group, and the first is boq-items
    public void First_table_in_nav_order_is_boq_items()
    {
        var first = All.OrderBy(e => e.GroupOrder).ThenBy(e => e.Order).First();
        Assert.Equal("boq-items", first.Key);

        foreach (var grp in All.GroupBy(e => e.Group))
        {
            var orders = grp.Select(e => e.Order).ToList();
            Assert.Equal(orders.Count, orders.Distinct().Count()); // no duplicate Order within a group
        }
    }

    [Fact] // (d) shared NAV metadata is identical within a group; SheetRef/Blurb are NOT required to match
    public void Nav_metadata_is_shared_within_a_group()
    {
        foreach (var grp in All.GroupBy(e => e.Group))
        {
            Assert.Single(grp.Select(e => e.GroupLabel).Distinct());
            Assert.Single(grp.Select(e => e.GroupOrder).Distinct());
        }

        // The periods group deliberately MIXES lineage (proof that SheetRef is per-entity, not shared):
        // its members do not all share one SheetRef.
        var periods = All.Where(e => e.Group == "periods").Select(e => e.SheetRef).Distinct().Count();
        Assert.True(periods > 1, "periods group should mix per-entity SheetRef (imported vs live-capture)");
    }

    [Fact] // (e) provenance is per-entity and accurate
    public void Sheet9_lineage_is_provenance_accurate()
    {
        // imported source inputs reference their real sheet
        foreach (var key in ImportedSheet9)
        {
            var e = All.Single(x => x.Key == key);
            Assert.Equal("9_HISTORICAL_DATA", e.SheetRef);
        }

        // cost-deltas sits in the periods group but is live-capture, NOT sheet-9-imported
        var deltas = All.Single(e => e.Key == "cost-deltas");
        Assert.Equal("periods", deltas.Group);
        Assert.NotEqual("9_HISTORICAL_DATA", deltas.SheetRef);
        Assert.Null(deltas.SheetRef);

        // no blurb mislabels the imported sheet-9 inputs as "derived, not imported"
        Assert.All(All, e =>
            Assert.DoesNotContain("derived, not imported", e.Blurb, System.StringComparison.OrdinalIgnoreCase));
    }
}
