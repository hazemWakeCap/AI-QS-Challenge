using QsEarlyWarning.Domain.Estimate;

namespace QsEarlyWarning.Core.Model;

/// <summary>
/// One priceable BOQ item reduced to what pricing a take-off needs.
/// </summary>
/// <param name="ItemRef">BOQ item reference, e.g. "2.04".</param>
/// <param name="Description">The item's description, shown so a rate can be argued with.</param>
/// <param name="Unit">Unit of measure — m², m³, Tonne, No. Pricing MUST agree with this.</param>
/// <param name="UnitRate">Direct+indirect cost per unit, in the project's reporting currency.</param>
/// <param name="BoqQuantity">The quantity Tower X priced, for context only — never used to price
/// another building's take-off.</param>
public sealed record RateItem(
    string ItemRef,
    string? Description,
    string? Unit,
    double UnitRate,
    double? BoqQuantity);

/// <summary>
/// The project's unit-rate library, projected from the BOQ.
///
/// <para><b>Why this exists.</b> A rate library is the one asset that transfers between projects.
/// Cost history does not — it belongs to the building that generated it — but "what does this
/// contractor charge per m³ of C40/50 column concrete" is reusable the moment you can measure
/// another building. That is what turns an IFC into a priced take-off.</para>
///
/// <para>A projection of `BoqLine`, not the raw estimate: only the fields pricing needs cross the
/// boundary, matching the rule the snapshot already follows for <see cref="TowerSpec"/> and the
/// resource mix.</para>
///
/// <para>Rates are <b>direct + indirect unit cost</b>, deliberately excluding margin and
/// contingency. Those are commercial decisions taken per project; carrying Tower X's margin into
/// another building's estimate would silently import a pricing position that was never agreed.</para>
/// </summary>
public sealed class RateBook
{
    private readonly Dictionary<string, RateItem> _byItemRef;

    public RateBook(IReadOnlyList<RateItem> items)
    {
        Items = items;
        _byItemRef = items.ToDictionary(i => i.ItemRef, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<RateItem> Items { get; }

    /// <summary>The rate for a BOQ item ref, or null when the item does not exist or is unpriced.</summary>
    public RateItem? Find(string itemRef) =>
        _byItemRef.TryGetValue(itemRef, out var item) ? item : null;

    /// <summary>
    /// Builds the library from the estimate. Lines with no positive unit rate are dropped — an
    /// item priced at zero cannot price anything, and letting it through would produce a confident
    /// AED 0 rather than an honest "no rate".
    /// </summary>
    public static RateBook From(EstimateModel estimate)
    {
        var items = estimate.BoqLines
            .Where(l => !string.IsNullOrWhiteSpace(l.ItemRef))
            .Select(l => new
            {
                l.ItemRef,
                l.Description,
                l.Unit,
                Rate = UnitRateOf(l),
                l.Quantity,
            })
            .Where(x => x.Rate is > 0)
            .GroupBy(x => x.ItemRef, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(x => new RateItem(x.ItemRef, x.Description, x.Unit, x.Rate!.Value, x.Quantity))
            .OrderBy(x => x.ItemRef, StringComparer.Ordinal)
            .ToList();

        return new RateBook(items);
    }

    /// <summary>
    /// Direct+indirect unit cost. The workbook carries it directly; where it is missing but the
    /// amount and quantity are present, it is recovered by division rather than dropping the item.
    /// </summary>
    private static double? UnitRateOf(BoqLine line)
    {
        if (line.DirectIndirectAmount is { } amount && line.Quantity is { } qty && qty > 0)
            return amount / qty;
        return null;
    }
}
