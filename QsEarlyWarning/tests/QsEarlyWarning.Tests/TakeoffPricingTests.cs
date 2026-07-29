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
    private const double SchoolSlabArea = 6761.8;

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

    // ── model quantity vs BOQ quantity ──
    //
    // The earliest overrun signal in the system: every other one waits for cost to be booked, this
    // one fires at design stage. Which is exactly why it has to refuse to manufacture a number.

    [Fact]
    public void Compares_the_measured_quantity_against_the_quantity_the_boq_priced()
    {
        var columns = Rates.Find("2.04")!;
        var result = Price(Line("IFCCOLUMN", TakeoffMeasure.Volume, SchoolColumnVolume, 203));

        var variance = Assert.Single(result.QuantityVariances);
        Assert.Equal("2.04", variance.BoqItemRef);
        Assert.Equal(SchoolColumnVolume, variance.ModelQuantity, 1);
        Assert.Equal(columns.BoqQuantity!.Value, variance.BoqQuantity, 1);
        Assert.Equal(variance.ModelQuantity - variance.BoqQuantity, variance.Variance, 1);
    }

    [Fact]
    public void Prices_the_variance_at_the_items_own_rate()
    {
        var columns = Rates.Find("2.04")!;
        var result = Price(Line("IFCCOLUMN", TakeoffMeasure.Volume, SchoolColumnVolume, 203));

        var variance = Assert.Single(result.QuantityVariances);
        Assert.Equal(variance.Variance * columns.UnitRate, variance.CostImpact, 0);
        // Sign convention is the whole point: more in the model than in the bill costs money.
        Assert.Equal(Math.Sign(variance.Variance), Math.Sign(variance.CostImpact));
    }

    [Fact]
    public void A_model_carrying_more_than_the_bill_reads_as_a_positive_overrun()
    {
        var columns = Rates.Find("2.04")!;
        var overBy = columns.BoqQuantity!.Value + 100;

        var result = Price(Line("IFCCOLUMN", TakeoffMeasure.Volume, overBy, 10));

        var variance = Assert.Single(result.QuantityVariances);
        Assert.Equal(100, variance.Variance, 1);
        Assert.True(variance.CostImpact > 0, "carrying more than was priced must cost money, not save it");
        Assert.Equal(100 / columns.BoqQuantity.Value, variance.VariancePct, 3);
    }

    [Fact]
    public void Reports_each_boq_item_once_carrying_the_sum_of_what_priced_through_it()
    {
        // The comparison is against the bill, so the row is per BOQ item and never per IFC class.
        // Anything that prices through one item is two parts of one number; splitting them would
        // report the same item as short twice.
        var result = Price(
            Line("IFCCOLUMN", TakeoffMeasure.Volume, SchoolColumnVolume, 203),
            Line("IFCSLAB", TakeoffMeasure.Volume, SchoolSlabVolume, 299),
            Line("IFCSLAB", TakeoffMeasure.Area, SchoolSlabArea, 0));

        Assert.NotEmpty(result.QuantityVariances);
        Assert.Equal(
            result.QuantityVariances.Select(v => v.BoqItemRef).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            result.QuantityVariances.Count);

        foreach (var variance in result.QuantityVariances)
        {
            double pricedThroughItem = result.Priced
                .Where(p => string.Equals(p.BoqItemRef, variance.BoqItemRef, StringComparison.OrdinalIgnoreCase))
                .Sum(p => p.Quantity);
            Assert.Equal(Math.Round(pricedThroughItem, 2), variance.ModelQuantity, 2);
        }
    }

    [Fact]
    public void Never_compares_against_a_boq_item_with_no_quantity()
    {
        // Treating a missing quantity as zero would turn every such item into a 100% overrun —
        // a confident number manufactured out of a gap in the bill.
        var rates = new RateBook(new[] { new RateItem("2.04", "columns, no qty", "m³", 1364.28, BoqQuantity: null) });

        var result = TakeoffPricer.Price(
            new[] { Line("IFCCOLUMN", TakeoffMeasure.Volume, SchoolColumnVolume, 203) },
            rates, "AED", 203);

        Assert.Empty(result.QuantityVariances);
        var skipped = Assert.Single(result.UncomparableQuantities);
        Assert.Equal("2.04", skipped.BoqItemRef);
        Assert.Contains("no quantity", skipped.Reason);
    }

    [Fact]
    public void Reports_nothing_to_compare_when_nothing_could_be_priced()
    {
        // Beams have no rule, so there is no BOQ item to compare them against and no variance
        // should be invented for them.
        var result = Price(Line("IFCBEAM", TakeoffMeasure.Volume, SchoolBeamVolume, 375));

        Assert.Empty(result.QuantityVariances);
        Assert.Empty(result.UncomparableQuantities);
    }

    [Fact]
    public void Orders_the_variances_by_the_money_at_stake()
    {
        var result = Price(
            Line("IFCCOLUMN", TakeoffMeasure.Volume, SchoolColumnVolume, 203),
            Line("IFCSLAB", TakeoffMeasure.Volume, SchoolSlabVolume, 299),
            Line("IFCSLAB", TakeoffMeasure.Area, SchoolSlabArea, 0));

        var impacts = result.QuantityVariances.Select(v => Math.Abs(v.CostImpact)).ToList();
        Assert.Equal(impacts.OrderByDescending(x => x).ToList(), impacts);
    }

    private static TakeoffLine Line(string cls, TakeoffMeasure measure, double qty, int count) =>
        new(cls, measure, qty, count, UnmeasuredCount: 0);

    private static TakeoffPricing Price(params TakeoffLine[] lines) =>
        TakeoffPricer.Price(lines, Rates, "AED", lines.Sum(l => l.ElementCount));
}
