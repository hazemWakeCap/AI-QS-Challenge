using QsEarlyWarning.Core.Model;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Domain.Estimate;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Phase 2 — the spatial layer: Zone_Area survives the load, zone rollups tie out to project BAC,
/// and the generated massing is genuinely derived from priced BOQ lines rather than invented.
/// </summary>
public sealed class SpatialCostMapTests
{
    private const long OwningId = 42;

    private static readonly CostCentrePeriod[] Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath).ToArray();

    private static readonly EstimateModel Estimate =
        new EstimateWorkbookLoader(TestData.WorkbookPath, OwningId).TryLoadForProject(OwningId)!;

    // ── the spatial attribute Phase 1 discarded ──

    [Fact]
    public void Zone_area_is_read_for_every_cost_centre()
    {
        var centres = Panel.GroupBy(r => r.BccId).ToList();
        Assert.Equal(173, centres.Count);

        var located = centres.Count(g => g.Any(r => !string.IsNullOrWhiteSpace(r.ZoneArea)));
        Assert.Equal(centres.Count, located);
    }

    [Fact]
    public void Zone_codes_match_the_workbook_vocabulary()
    {
        var byZone = Panel
            .Where(r => r.PeriodId == 12)
            .GroupBy(r => r.ZoneArea!)
            .ToDictionary(g => g.Key, g => g.Count());

        // Exact expected shape — a silent change to any of these moves money on the model.
        Assert.Equal(72, byZone["FLOORS-ALL"]);
        Assert.Equal(21, byZone["EXTERNAL"]);
        Assert.Equal(18, byZone["STRUCTURE"]);
        Assert.Equal(18, byZone["FLOORS-B2-RF"]);
        Assert.Equal(11, byZone["BASEMENT"]);
        Assert.Equal(10, byZone.Count);
    }

    [Fact]
    public void Compound_zone_is_kept_whole_not_split()
    {
        // BASEMENT+EXT spans two places, but the data carries no basis for allocating its budget
        // between them. It stays one zone precisely so no fabricated split reaches the screen.
        var compound = Panel.Where(r => r.PeriodId == 12 && r.ZoneArea == "BASEMENT+EXT").ToList();
        Assert.Equal(5, compound.Count);
        Assert.DoesNotContain(Panel, r => r.ZoneArea is "BASEMENT+EXT_A" or "BASEMENT+EXT_B");
    }

    // ── the tie-out contract the cost map promises ──

    [Fact]
    public void Zone_rollup_ties_out_to_project_bac()
    {
        var rows = Panel.Where(r => r.PeriodId == 12).ToList();
        double projectBac = rows.Sum(r => r.BacAed ?? 0);

        double zoned = rows.Where(r => !string.IsNullOrWhiteSpace(r.ZoneArea)).Sum(r => r.BacAed ?? 0);
        double unmapped = rows.Where(r => string.IsNullOrWhiteSpace(r.ZoneArea)).Sum(r => r.BacAed ?? 0);

        Assert.Equal(224_322_886d, projectBac, 2);
        Assert.Equal(projectBac, zoned + unmapped, 2);
    }

    [Fact]
    public void Aggregate_zone_cpi_is_sum_ev_over_sum_ac_not_a_mean_of_ratios()
    {
        var structure = Panel.Where(r => r.PeriodId == 12 && r.ZoneArea == "STRUCTURE").ToList();

        double aggregate = structure.Sum(r => r.EvAed ?? 0) / structure.Sum(r => r.AcCumulative ?? 0);
        double meanOfRatios = structure.Where(r => r.Cpi is not null).Average(r => r.Cpi!.Value);

        Assert.Equal(0.9396, aggregate, 4);
        // The two genuinely differ — which is why the aggregate is the one that ships.
        Assert.NotEqual(Math.Round(aggregate, 4), Math.Round(meanOfRatios, 4));
    }

    [Fact]
    public void A_zone_can_read_green_while_holding_amber_centres()
    {
        // The finding that justifies the click-through: FLOORS-ALL rolls up above the 0.95 AMBER
        // threshold while containing AMBER cost centres and the project's largest unspent balance.
        var floors = Panel.Where(r => r.PeriodId == 12 && r.ZoneArea == "FLOORS-ALL").ToList();

        double cpi = floors.Sum(r => r.EvAed ?? 0) / floors.Sum(r => r.AcCumulative ?? 0);
        int amber = floors.Count(r => string.Equals(r.AlertLevel, "AMBER", StringComparison.OrdinalIgnoreCase));

        Assert.True(cpi >= 0.95, $"zone rollup should read green, was {cpi:F4}");
        Assert.Equal(11, amber);
    }

    // ── the generated massing is derived, not invented ──

    [Fact]
    public void Every_massing_dimension_cites_a_boq_line()
    {
        var spec = TowerSpecDeriver.Derive(Estimate);

        Assert.True(spec.Derived);
        Assert.NotEmpty(spec.Dimensions);
        Assert.All(spec.Dimensions, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Derivation));
            Assert.False(string.IsNullOrWhiteSpace(d.SourceItemRef));
        });
    }

    [Fact]
    public void Floor_count_comes_from_the_line_priced_per_floor()
    {
        var spec = TowerSpecDeriver.Derive(Estimate);

        Assert.Equal(6, spec.FloorCount);
        var floors = spec.Dimensions.Single(d => d.Key == "floorCount");
        Assert.Equal("9.07", floors.SourceItemRef);
    }

    [Fact]
    public void Floor_plate_is_the_soffit_formwork_spread_over_the_floors()
    {
        var spec = TowerSpecDeriver.Derive(Estimate);
        var plate = spec.Dimensions.Single(d => d.Key == "floorPlateArea");

        Assert.Equal("2.11", plate.SourceItemRef);
        Assert.Equal(21_500d / 6, plate.Value, 0);
        // Footprint is the square root of the plate — the shape adds no information the BOQ lacks.
        Assert.Equal(Math.Sqrt(plate.Value), spec.FootprintWidthM, 0);
    }

    [Fact]
    public void Assumed_dimensions_say_so_rather_than_claiming_derivation()
    {
        // The BOQ evidences a dug substructure but never prices a level count. The spec must admit
        // that instead of presenting 2 as though it fell out of the data.
        var spec = TowerSpecDeriver.Derive(Estimate);
        var basements = spec.Dimensions.Single(d => d.Key == "basementLevels");

        Assert.Contains("assumption", basements.Derivation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_an_estimate_the_spec_declares_itself_underived()
    {
        var spec = TowerSpecDeriver.Derive(null);

        Assert.False(spec.Derived);
        Assert.Contains("NOT derived", spec.Provenance);
    }
}
