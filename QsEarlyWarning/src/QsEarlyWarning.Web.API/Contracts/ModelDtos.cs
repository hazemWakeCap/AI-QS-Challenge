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
