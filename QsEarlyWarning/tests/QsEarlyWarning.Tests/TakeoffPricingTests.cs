using QsEarlyWarning.Core.Model;
using QsEarlyWarning.Domain.Estimate;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Phase 2 — pricing a model take-off with the project's own rate library.
///
/// The claim under test is narrow and must stay narrow: quantities measured off a model are priced
/// at this project's unit rates, and everything that could NOT be priced is reported rather than
/// quietly dropped. A priced total that hides its residual would be the dishonest version of this
/// feature, so the residual is asserted as hard as the arithmetic.
/// </summary>
public sealed class TakeoffPricingTests
{
    private const long OwningId = 42;

    private static readonly EstimateModel Estimate =
        new EstimateWorkbookLoader(TestData.WorkbookPath, OwningId).TryLoadForProject(OwningId)!;

    private static readonly RateBook Rates = RateBook.From(Estimate);

    // Quantities measured off the bundled school_str.ifc — see SpatialCostMapTests for the
    // Tower X side. These are the real figures the viewer produces.
    private const double SchoolColumnVolume = 112.3;
    private const double SchoolSlabVolume = 2735.5;
    private const double SchoolBeamVolume = 358.8;

    // ── the rate library ──

    [Fact]
    public void Rate_book_carries_the_structural_items_a_takeoff_needs()
    {
        Assert.NotNull(Rates.Find("2.04"));   // columns, m³
        Assert.NotNull(Rates.Find("2.06"));   // suspended slab, m³
        Assert.NotNull(Rates.Find("2.11"));   // soffit formwork, m²
        Assert.All(Rates.Items, i => Assert.True(i.UnitRate > 0, $"{i.ItemRef} has a non-positive rate"));
    }

    [Fact]
    public void Rate_book_recovers_the_unit_rate_from_the_priced_amount()
    {
        // The workbook's displayed unit cost is rounded to 2dp; dividing the amount by the quantity
        // recovers the rate the amount was actually built from.
        var columns = Rates.Find("2.04")!;
        Assert.Equal("m³", columns.Unit);
        Assert.Equal(1364.28, columns.UnitRate, 1);
    }

    // ── pricing ──

    [Fact]
    public void Prices_measured_quantities_at_the_projects_own_rates()
    {
        var result = Price(
            Line("IFCCOLUMN", TakeoffMeasure.Volume, SchoolColumnVolume, 203),
            Line("IFCSLAB", TakeoffMeasure.Volume, SchoolSlabVolume, 299));

        var columns = result.Priced.Single(p => p.IfcClass == "IFCCOLUMN");
        Assert.Equal("2.04", columns.BoqItemRef);
        Assert.Equal(SchoolColumnVolume * columns.UnitRate, columns.Amount, 2);

        var slabs = result.Priced.Single(p => p.IfcClass == "IFCSLAB");
        Assert.Equal("2.06", slabs.BoqItemRef);

        Assert.Equal(columns.Amount + slabs.Amount, result.PricedAmount, 2);
    }

    [Fact]
    public void A_class_with_no_boq_item_is_reported_not_approximated()
    {
        // Tower X prices no beam concrete. The temptation is to price beams at the slab rate;
        // doing so would invent money the estimate never contained.
        var result = Price(Line("IFCBEAM", TakeoffMeasure.Volume, SchoolBeamVolume, 375));

        Assert.Empty(result.Priced);
        Assert.Equal(0, result.PricedAmount);

        var beams = result.Unpriced.Single();
        Assert.Equal(SchoolBeamVolume, beams.Quantity);
        Assert.Equal(375, beams.ElementCount);
        Assert.Contains("no beam concrete", beams.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_class_the_model_could_not_measure_is_reported_with_its_element_count()
    {
        // 619 rebar elements carry no quantity at all in the sample model.
        var result = Price(Line("IFCREINFORCINGBAR", TakeoffMeasure.Volume, 0, 619));

        Assert.Empty(result.Priced);
        var rebar = result.Unpriced.Single();
        Assert.Equal(619, rebar.ElementCount);
        Assert.Contains("no measurable quantity", rebar.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_element_is_accounted_for_and_the_tie_out_can_actually_fail()
    {
        var lines = new[]
        {
            Line("IFCCOLUMN", TakeoffMeasure.Volume, SchoolColumnVolume, 203),
            Line("IFCBEAM", TakeoffMeasure.Volume, SchoolBeamVolume, 375),
            Line("IFCREINFORCINGBAR", TakeoffMeasure.Volume, 0, 619),
        };

        var honest = TakeoffPricer.Price(lines, Rates, "AED", modelElementCount: 203 + 375 + 619);
        Assert.True(honest.TiesOut);
        Assert.Equal(203, honest.PricedElements);
        Assert.Equal(375 + 619, honest.UnpricedElements);

        // The guard is only worth having if it can go off: hide an element from the take-off and
        // the tie-out must notice, rather than agreeing with its own arithmetic.
        var lossy = TakeoffPricer.Price(lines, Rates, "AED", modelElementCount: 203 + 375 + 619 + 24);
        Assert.False(lossy.TiesOut);
    }

    [Fact]
    public void Area_and_volume_of_the_same_class_price_against_different_items()
    {
        var result = Price(
            Line("IFCSLAB", TakeoffMeasure.Volume, SchoolSlabVolume, 299),
            Line("IFCSLAB", TakeoffMeasure.Area, 6761.8, 0));

        Assert.Equal("2.06", result.Priced.Single(p => p.Measure == TakeoffMeasure.Volume).BoqItemRef);
        Assert.Equal("2.11", result.Priced.Single(p => p.Measure == TakeoffMeasure.Area).BoqItemRef);
    }

    [Fact]
    public void Rules_are_returned_so_the_pairing_can_be_audited()
    {
        var result = Price(Line("IFCCOLUMN", TakeoffMeasure.Volume, 1, 1));

        Assert.NotEmpty(result.RulesApplied);
        Assert.All(result.RulesApplied, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.BoqItemRef));
            Assert.False(string.IsNullOrWhiteSpace(r.Rationale));
        });
    }

    // ── helpers ──

    private static TakeoffLine Line(string cls, TakeoffMeasure measure, double qty, int count) =>
        new(cls, measure, qty, count, UnmeasuredCount: 0);

    private static TakeoffPricing Price(params TakeoffLine[] lines) =>
        TakeoffPricer.Price(lines, Rates, "AED", lines.Sum(l => l.ElementCount));
}
