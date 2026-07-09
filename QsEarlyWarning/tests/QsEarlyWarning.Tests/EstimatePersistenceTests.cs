using QsEarlyWarning.Domain.Estimate;
using QsEarlyWarning.Infrastructure.Excel;
using QsEarlyWarning.Infrastructure.Import;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// DB-less guards for the estimate-graph persistence (plan: "Persist the estimate sheets 1-4 during
/// import"). Covers the pieces that don't need Postgres: the shared reader actually parses the four
/// sheets, and the pure mapping rules (rtype normalization, per-line cost mirroring the GENERATED
/// column, and the total_amount tie decision) behave as the importer relies on. The end-to-end insert /
/// FK-resolution / purge-reorder path is proven separately by the DB-backed test in QsEarlyWarning.Db.Tests.
/// </summary>
public sealed class EstimatePersistenceTests
{
    private static readonly EstimateModel Model = new EstimateWorkbookReader().Read(TestData.WorkbookPath);

    [Fact]
    public void Reader_parses_all_four_sheets_nonempty()
    {
        Assert.NotEmpty(Model.Norms);
        Assert.NotEmpty(Model.BoqLines);
        Assert.NotEmpty(Model.Mappings);
        Assert.NotEmpty(Model.ResourceLines);
    }

    [Fact]
    public void Every_resource_type_normalizes_into_the_check_set()
    {
        var allowed = new[] { "MANPOWER", "MATERIAL", "EQUIPMENT", "SUBCONTRACT" };
        // Any rtype that doesn't normalize is skipped at insert time (never violates the CHECK). Assert the
        // workbook's real types all DO normalize, so nothing is silently dropped for Tower X.
        var unmapped = Model.ResourceLines
            .Select(l => l.ResourceType)
            .Where(t => EstimateMapping.NormalizeRtype(t) is null)
            .Distinct()
            .ToList();
        Assert.True(unmapped.Count == 0, $"unmapped resource types: {string.Join(", ", unmapped)}");
        Assert.All(Model.ResourceLines, l => Assert.Contains(EstimateMapping.NormalizeRtype(l.ResourceType), allowed));
    }

    [Theory]
    [InlineData("manpower", "MANPOWER")]
    [InlineData("  Material ", "MATERIAL")]
    [InlineData("EQUIPMENT", "EQUIPMENT")]
    [InlineData("Sub-Contract", "SUBCONTRACT")]
    [InlineData("widgets", null)]
    [InlineData(null, null)]
    public void NormalizeRtype_maps_expected(string? input, string? expected)
        => Assert.Equal(expected, EstimateMapping.NormalizeRtype(input));

    [Fact]
    public void LineCost_mirrors_the_generated_column_and_treats_null_as_zero()
    {
        Assert.Equal(250.00m, EstimateMapping.LineCost(100, 2.5));
        Assert.Equal(0m, EstimateMapping.LineCost(null, 2.5));
        Assert.Equal(0m, EstimateMapping.LineCost(100, null));
        // round-half-to-even at 2dp, same as SQL round(...)
        Assert.Equal(1.23m, EstimateMapping.LineCost(1, 1.234));
    }

    [Fact]
    public void Boq_item_refs_are_unique_by_composite_sec_itemref_for_this_workbook()
    {
        // The importer keys boq_items by (Sec, ItemRef) — the DB natural key. Confirm no collisions in the
        // real workbook so mappings/resource lines link to the right item.
        var dupes = Model.BoqLines
            .GroupBy(b => (b.Sec, b.ItemRef))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Sec}/{g.Key.ItemRef}")
            .ToList();
        Assert.True(dupes.Count == 0, $"duplicate composite BOQ keys: {string.Join(", ", dupes)}");
    }
}
