namespace QsEarlyWarning.Web.API.Contracts;

/// <summary>
/// One physical zone of the building, with the money of every cost centre mapped to it.
///
/// <para><b>Cpi is null, not a number, when too little money has flowed to judge the zone.</b> At
/// period 12 the EXTERNAL zone is 0.68% spent (AED 32k of AC against AED 4.8M of BAC). Its ratio
/// computes cleanly, but a CPI resting on 0.68% of the budget carries nowhere near the confidence
/// of one resting on STRUCTURE's 78%, and painting both the same colour would overstate what we
/// know. Below the materiality floor the zone reports <see cref="CostSufficient"/> = false and the
/// viewer paints it "insufficient cost" rather than green.</para>
/// </summary>
/// <param name="ZoneCode">Verbatim Zone_Area tag (STRUCTURE, FLOORS-B2-RF, BASEMENT+EXT, …).</param>
/// <param name="Bac">Budget at completion for the zone.</param>
/// <param name="Pv">Planned value to date.</param>
/// <param name="Ev">Earned value to date.</param>
/// <param name="Ac">Actual cost to date.</param>
/// <param name="Unspent">BAC − AC: the money in this zone that has NOT yet been spent. The
/// headline number — this is what is still saveable if the QS acts now.</param>
/// <param name="Cpi">ΣEV/ΣAC for the zone, or null when <paramref name="CostSufficient"/> is false.
/// Never a mean of per-centre CPIs.</param>
/// <param name="Spi">ΣEV/ΣPV for the zone, or null when PV is zero.</param>
/// <param name="CostSufficient">Whether AC clears the materiality floor (1% of BAC) at which a
/// ratio is worth showing.</param>
/// <param name="AlertLevel">GREEN | AMBER | NOT_STARTED | INSUFFICIENT_COST — how to paint it.</param>
/// <param name="CentreCount">Cost centres mapped to this zone.</param>
/// <param name="AmberCount">How many of them are AMBER at this period.</param>
/// <param name="TopRiskBccId">Worst centre in the zone by CPI (the click-through target).</param>
/// <param name="TopRiskCpi">That centre's CPI.</param>
public sealed record ZoneCostDto(
    string ZoneCode,
    decimal Bac,
    decimal Pv,
    decimal Ev,
    decimal Ac,
    decimal Unspent,
    double? Cpi,
    double? Spi,
    bool CostSufficient,
    string AlertLevel,
    int CentreCount,
    int AmberCount,
    string? TopRiskBccId,
    double? TopRiskCpi);

/// <summary>
/// The cost map for one reporting period: every zone, plus an explicit residual.
///
/// <para><b>Tie-out contract:</b> <c>Σ Zones[].Bac + UnmappedBac == ProjectBac</c>, exactly. This
/// mirrors the variance bridge's <c>Σ CV_r + residual == CV</c>. Money belonging to cost centres
/// with no Zone_Area is surfaced in <see cref="UnmappedBac"/> — never dropped, never spread across
/// zones on a made-up allocation.</para>
/// </summary>
public sealed record CostMapDto(
    string ProjectSlug,
    int Period,
    int MinPeriod,
    int MaxPeriod,
    string Currency,
    decimal ProjectBac,
    decimal ProjectAc,
    decimal UnmappedBac,
    int UnmappedCentreCount,
    /// <summary>
    /// BAC − AC summed over zones whose CPI is below the product's frozen AMBER threshold (0.95).
    /// Money already committed to zones that are measurably drifting, but not yet spent — the
    /// window a QS can still act in. Uses <c>EvmThresholds.CpiThreshold</c> and nothing looser:
    /// widening the threshold to flatter the number would be moving the goalposts.
    /// </summary>
    decimal UnspentInDriftingZones,
    IReadOnlyList<ZoneCostDto> Zones);

/// <summary>
/// One derived dimension of the generated Tower X massing, carrying the BOQ line it came from.
///
/// <para>Tower X has no published model — the organisers said so. Rather than invent geometry, every
/// dimension is derived from a priced BOQ line and ships with its provenance so the derivation can
/// be argued with on screen instead of taken on trust.</para>
/// </summary>
/// <param name="Key">Machine name (floorCount, floorPlateArea, …).</param>
/// <param name="Label">Human label for the provenance table.</param>
/// <param name="Value">The derived number.</param>
/// <param name="Unit">Its unit (floors, m², m³, m).</param>
/// <param name="SourceItemRef">BOQ Item Ref the value came from, e.g. "9.07".</param>
/// <param name="SourceDescription">That BOQ line's description.</param>
/// <param name="Derivation">How the number was reached, in words.</param>
public sealed record GeometryDimensionDto(
    string Key,
    string Label,
    double Value,
    string Unit,
    string? SourceItemRef,
    string? SourceDescription,
    string Derivation);

// ── Model take-off pricing ────────────────────────────────────────────────────

/// <summary>One measured quantity posted from the viewer.</summary>
/// <param name="IfcClass">Upper-case IFC entity name, e.g. IFCCOLUMN.</param>
/// <param name="Measure">"volume" or "area".</param>
/// <param name="Quantity">Measured amount, in m³ or m².</param>
/// <param name="ElementCount">Elements that contributed the measurement.</param>
/// <param name="UnmeasuredCount">Elements of this class carrying no such measurement.</param>
public sealed record TakeoffLineRequest(
    string IfcClass,
    string Measure,
    double Quantity,
    int ElementCount,
    int UnmeasuredCount);

/// <summary>A take-off measured off a model, awaiting a price.</summary>
/// <param name="Lines">The measured quantities, aggregated by IFC class.</param>
/// <param name="ModelElementCount">How many elements the model contains in total. Reported
/// independently of the lines so the response's tie-out can actually catch elements that fell
/// out of the take-off, rather than comparing a sum against itself.</param>
public sealed record PriceTakeoffRequest(
    IReadOnlyList<TakeoffLineRequest> Lines,
    int ModelElementCount);

public sealed record PricedLineDto(
    string IfcClass, string Measure, double Quantity, string Unit, int ElementCount,
    string BoqItemRef, string? BoqDescription, double UnitRate, decimal Amount, string Rationale);

public sealed record UnpricedLineDto(
    string IfcClass, string Measure, double Quantity, int ElementCount, string Reason);

public sealed record TakeoffRuleDto(
    string IfcClass, string Measure, string Unit, string BoqItemRef, string Rationale);

/// <summary>
/// A priced model take-off.
///
/// <para><see cref="PricedAmount"/> must never be shown alone. It is the cost of the part of the
/// building that could be both measured and priced; the unpriced list is what it excludes, and the
/// element counts tie out so nothing can quietly vanish between them.</para>
/// </summary>
public sealed record TakeoffPricingDto(
    string ProjectSlug,
    string Currency,
    decimal PricedAmount,
    IReadOnlyList<PricedLineDto> Priced,
    IReadOnlyList<UnpricedLineDto> Unpriced,
    int TotalElements,
    int PricedElements,
    int UnpricedElements,
    int UnmeasuredElements,
    bool TiesOut,
    IReadOnlyList<TakeoffRuleDto> RulesApplied,
    string RateBasis,
    IReadOnlyList<QuantityVarianceDto> QuantityVariances,
    IReadOnlyList<UncomparableQuantityDto> UncomparableQuantities,
    string VarianceBasis);

/// <summary>What the model measures for a BOQ item versus what that item was priced for.</summary>
public sealed record QuantityVarianceDto(
    string BoqItemRef,
    string? BoqDescription,
    string Unit,
    double ModelQuantity,
    double BoqQuantity,
    double Variance,
    double VariancePct,
    double UnitRate,
    decimal CostImpact);

/// <summary>A priced item the model could not be compared against, and why.</summary>
public sealed record UncomparableQuantityDto(string BoqItemRef, string Reason);

/// <summary>
/// The authored IFC-element → BOQ-item register, joined to the bill and to the cost centres.
///
/// <para>Deliberately has no period parameter. The register is static, so the client fetches it once
/// and scrubbing the period rejoins against the cost-centre array it already holds rather than
/// refetching a hundred kilobytes of geometry bindings.</para>
///
/// <para>Normalised on purpose: <see cref="Items"/> and <see cref="Rules"/> hold what would
/// otherwise repeat on every one of ~1,600 element rows.</para>
/// </summary>
public sealed record ElementMapDto(
    string ProjectSlug,
    string Currency,
    IReadOnlyList<MappedElementDto> Elements,
    IReadOnlyList<MappedItemDto> Items,
    IReadOnlyList<MappingRuleDto> Rules,
    IReadOnlyList<UnmappedClassDto> Unmapped,
    int MappedElements,
    int TotalElements,
    string MappingBasis);

/// <param name="BoqItemRefs">Empty means the bill prices nothing for this element.</param>
/// <param name="Confidence">The weakest binding this element rests on.</param>
public sealed record MappedElementDto(
    string GlobalId, string IfcClass, string? Storey,
    IReadOnlyList<string> BoqItemRefs, double Confidence);

/// <summary>A BOQ item the model reaches, with the cost centre it is the same thing as.</summary>
/// <param name="BccId">Resolved through <c>WBS_Code</c>, which IS the BOQ item ref — a real 1:1 in
/// the source data, not an authored link.</param>
public sealed record MappedItemDto(
    string BoqItemRef, string? Description, string? Unit,
    double UnitRate, double? BoqQuantity, string? BccId);

/// <summary>One declared class → item binding, shown so a QS can disagree with it.</summary>
public sealed record MappingRuleDto(
    string IfcClass, string BoqItemRef, string Role, string Basis, double Confidence, int ElementCount);

/// <summary>A class the model contains that the bill prices nothing for.</summary>
public sealed record UnmappedClassDto(string IfcClass, int ElementCount, string Reason);

/// <summary>The parametric massing spec plus the provenance table that justifies it.</summary>
public sealed record GeometrySpecDto(
    string ProjectSlug,
    int FloorCount,
    int BasementLevels,
    double FootprintWidthM,
    double FootprintDepthM,
    double FloorHeightM,
    double BasementDepthM,
    double CoreWidthM,
    double CoreDepthM,
    bool Derived,
    string Provenance,
    IReadOnlyList<GeometryDimensionDto> Dimensions);
