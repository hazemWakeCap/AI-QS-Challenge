using QsEarlyWarning.Domain.Estimate;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// The authored IFC-element → BOQ-item register.
///
/// <para>One hop in this system is a human judgement rather than source data: which BOQ item an IFC
/// element consumes. Everything downstream of it is real — the item ref IS the cost centre's
/// <c>WBS_Code</c> — so the whole chain's credibility rests on the register pointing only at things
/// that actually exist. That is what these tests defend.</para>
///
/// <para>They read the committed CSV, not a fixture, so hand-editing the register to reference an
/// item the bill does not contain fails the build rather than surfacing a dead link in the UI.</para>
/// </summary>
public sealed class IfcElementMapTests
{
    private const long OwningId = 42;

    private static readonly IfcElementMap Map = IfcElementMapCsvLoader.Load(TestData.ElementMapPath);

    private static readonly EstimateModel Estimate =
        new EstimateWorkbookLoader(TestData.WorkbookPath, OwningId).TryLoadForProject(OwningId)!;

    private static readonly IReadOnlyList<Domain.Entities.CostCentrePeriod> Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath).ToArray();

    // ── the join that everything downstream rests on ──

    [Fact]
    public void Every_mapped_item_exists_in_the_bill()
    {
        var billItems = Estimate.BoqLines
            .Select(l => l.ItemRef)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var dangling = Map.BoqItemRefs.Where(r => !billItems.Contains(r)).ToList();

        Assert.True(dangling.Count == 0,
            $"The register points at BOQ items that do not exist: {string.Join(", ", dangling)}");
    }

    [Fact]
    public void Every_mapped_item_resolves_to_a_cost_centre()
    {
        // WBS_Code IS the BOQ item ref — a real 1:1 in the source data. This asserts the register
        // only rides on that join where it actually holds.
        var byWbs = Panel
            .Where(p => !string.IsNullOrWhiteSpace(p.WbsCode))
            .Select(p => p.WbsCode!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unreachable = Map.BoqItemRefs.Where(r => !byWbs.Contains(r)).ToList();

        Assert.True(unreachable.Count == 0,
            $"Mapped BOQ items reach no cost centre: {string.Join(", ", unreachable)}");
    }

    [Fact]
    public void The_boq_item_ref_and_the_wbs_code_are_the_same_key()
    {
        // The finding the whole design rests on, pinned so a future workbook cannot quietly break it.
        var billItems = Estimate.BoqLines.Select(l => l.ItemRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wbsCodes = Panel.Where(p => !string.IsNullOrWhiteSpace(p.WbsCode))
                            .Select(p => p.WbsCode!.Trim())
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(billItems.Count, wbsCodes.Count);
        Assert.True(billItems.SetEquals(wbsCodes),
            "BOQ item refs and cost-centre WBS codes are no longer the same set — the element→money "
            + "chain has lost the join it depends on.");
    }

    [Fact]
    public void One_cost_centre_per_bill_item()
    {
        var multi = Panel
            .Where(p => !string.IsNullOrWhiteSpace(p.WbsCode))
            .GroupBy(p => p.WbsCode!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.BccId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(multi.Count == 0, $"Item refs mapping to several cost centres: {string.Join(", ", multi)}");
    }

    // ── the register's own shape ──

    [Fact]
    public void Covers_the_model_and_says_what_it_does_not_cover()
    {
        Assert.Equal(1526, Map.TotalCount);
        Assert.Equal(1127, Map.MappedCount);

        // The gap is carried in the register rather than recomputed, so it cannot drift from it.
        Assert.Equal(399, Map.Unmapped.Sum(u => u.ElementCount));
        Assert.Equal(Map.TotalCount, Map.MappedCount + Map.Unmapped.Sum(u => u.ElementCount));
    }

    [Fact]
    public void Reports_beams_as_scope_the_bill_never_priced()
    {
        // The most valuable thing the register says: 375 elements of real work with no bill item.
        var beams = Assert.Single(Map.Unmapped, u => u.IfcClass == "IFCBEAM");
        Assert.Equal(375, beams.ElementCount);
        Assert.Contains("no beam", beams.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unmapped_element_reaches_no_bill_item_at_all()
    {
        // Rather than being absent from the register — the difference between "we did not look"
        // and "we looked and the bill prices nothing".
        var unmappedElements = Map.Elements.Where(e => e.BoqItemRefs.Count == 0).ToList();
        Assert.Equal(399, unmappedElements.Count);
        Assert.All(unmappedElements, e => Assert.Equal(0, e.Confidence));
    }

    [Fact]
    public void An_element_carries_every_item_it_consumes()
    {
        // A slab is concrete AND its soffit formwork. Collapsing that to one item would understate
        // what a single element commits.
        var slab = Map.Elements.First(e => e.IfcClass == "IFCSLAB");
        Assert.Equal(new[] { "2.06", "2.11" }, slab.BoqItemRefs.OrderBy(r => r).ToArray());
    }

    [Fact]
    public void Rebar_is_the_only_thing_carried_at_reduced_confidence()
    {
        // The bar-to-host relationship does not exist in the file, so rebar is placed by storey —
        // a weaker claim, and it must stay visibly weaker than a direct class match.
        var weak = Map.Rules.Where(r => r.Confidence < 0.9).ToList();
        Assert.All(weak, r => Assert.Equal("IFCREINFORCINGBAR", r.IfcClass));
        Assert.All(weak, r => Assert.Contains("no bar-to-host", r.Basis, StringComparison.OrdinalIgnoreCase));

        var rebar = Map.Elements.First(e => e.IfcClass == "IFCREINFORCINGBAR");
        Assert.Equal(0.6, rebar.Confidence, 3);
    }

    [Fact]
    public void Every_rule_explains_itself()
    {
        // The bindings are judgements, so each one ships the sentence a QS would argue with.
        Assert.NotEmpty(Map.Rules);
        Assert.All(Map.Rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Basis), $"{r.IfcClass}→{r.BoqItemRef} has no basis");
            Assert.True(r.ElementCount > 0);
        });
    }

    [Fact]
    public void Commas_inside_a_rationale_survive_the_parser()
    {
        // "Column concrete, priced per m3 of column." is quoted in the CSV; a naive Split(',')
        // would shear it and silently truncate every explanation in the register.
        var rule = Map.Rules.First(r => r.BoqItemRef == "2.04");
        Assert.Contains(",", rule.Basis);
        Assert.EndsWith(".", rule.Basis);
    }
}
