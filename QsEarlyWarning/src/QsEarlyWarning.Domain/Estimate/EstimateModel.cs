namespace QsEarlyWarning.Domain.Estimate;

// Idea-3 Estimate Assumption Stress Test — the joined estimate graph read from workbook sheets 1-4.
// Lives in Domain (not Core/Infrastructure) so the Infrastructure loader and the Core engine can both
// reference it without a circular project dependency (Core → Domain + Infrastructure; Infrastructure →
// Domain). Fields match the verified workbook schema (see the integration plan's schema section).
//
// Amounts are AED. Percentages (Margin %, Cont %) are percentage POINTS (e.g. 22, 8), not fractions.
// Missing/sentinel cells parse to null, never a value.

/// <summary>2_ESTIMATE_NORMS — one estimating recipe. <c>OutputNorm</c> is units produced per gang-shift.</summary>
public sealed record EstimateNorm(
    string NormCode, string? DiscCode, string? DisciplineName, string? SubTradeCode, string? SubTradeName,
    string? Unit, double? OutputNorm, string? ProcurementRoute, string? GangComposition, double? GangSize,
    double? Mat1QtyPerUoW, double? Mat2QtyPerUoW, string? Notes);

/// <summary>1_BOQ — one priced work item. <c>TotalAmount</c> = Direct+Indirect+Margin+Cont.</summary>
public sealed record BoqLine(
    string Sec, string ItemRef, string? Description, string? Unit, double? Quantity,
    double? DirectIndirectAmount, double? MarginPct, double? MarginAmount, double? ContPct,
    double? ContingencyAmount, double? TotalAmount, string? NormRef);

/// <summary>3_BOQ_MAPPING — BOQ line → norm → estimate package. Authoritative item <c>Unit</c> + procurement.</summary>
public sealed record BoqMapping(
    string Sec, string ItemRef, string? Unit, string? NormCode, string? EstimatePackage,
    string? OpCode, string? PrimaryResourceTypes, string? Procurement);

/// <summary>4_ESTIMATE_DATASHEET — one resource line of a BOQ item. Unit rates + costs live here.</summary>
public sealed record ResourceLine(
    string Sec, string ItemRef, string? NormCode, string? Package, string? OpCode, string ResourceType,
    string? ResourceDescription, string? Unit, double? BoqQty, double? QtyPerUnitWork, string? ConsumptionUnit,
    double? TotalResourceQty, double? UnitRate, double? ResourceCost, double? IndirectCost,
    double? TotalContractAmt, double? GangOutput, double? GangSize);

/// <summary>The immutable joined estimate graph with the lookups the stress-test engine needs.</summary>
public sealed class EstimateModel
{
    public IReadOnlyList<EstimateNorm> Norms { get; }
    public IReadOnlyList<BoqLine> BoqLines { get; }
    public IReadOnlyList<BoqMapping> Mappings { get; }
    public IReadOnlyList<ResourceLine> ResourceLines { get; }

    /// <summary>Norm by <c>Norm Code</c> (first wins on the rare duplicate).</summary>
    public IReadOnlyDictionary<string, EstimateNorm> NormByCode { get; }
    /// <summary>BOQ line by <c>Item Ref</c> (e.g. "1.01") — the key sheet-9 WBS_Code joins on.</summary>
    public IReadOnlyDictionary<string, BoqLine> BoqByItemRef { get; }
    /// <summary>Mapping by <c>Item</c> — the authoritative unit + package + procurement per item.</summary>
    public IReadOnlyDictionary<string, BoqMapping> MappingByItemRef { get; }
    /// <summary>Resource lines grouped by <c>Item</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ResourceLine>> ResourceLinesByItemRef { get; }

    public EstimateModel(
        IReadOnlyList<EstimateNorm> norms, IReadOnlyList<BoqLine> boqLines,
        IReadOnlyList<BoqMapping> mappings, IReadOnlyList<ResourceLine> resourceLines)
    {
        Norms = norms;
        BoqLines = boqLines;
        Mappings = mappings;
        ResourceLines = resourceLines;

        var normByCode = new Dictionary<string, EstimateNorm>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in norms) normByCode.TryAdd(n.NormCode, n);
        NormByCode = normByCode;

        var boqByItem = new Dictionary<string, BoqLine>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in boqLines) boqByItem.TryAdd(b.ItemRef, b);
        BoqByItemRef = boqByItem;

        var mapByItem = new Dictionary<string, BoqMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappings) mapByItem.TryAdd(m.ItemRef, m);
        MappingByItemRef = mapByItem;

        var linesByItem = new Dictionary<string, List<ResourceLine>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resourceLines)
        {
            if (!linesByItem.TryGetValue(r.ItemRef, out var list))
                linesByItem[r.ItemRef] = list = new List<ResourceLine>();
            list.Add(r);
        }
        ResourceLinesByItemRef = linesByItem.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<ResourceLine>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }
}
