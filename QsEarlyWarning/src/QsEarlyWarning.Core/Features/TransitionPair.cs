namespace QsEarlyWarning.Core.Features;

/// <summary>
/// One engineered transition pair: features at period p → label at p+1.
/// Population is GREEN-at-p only (plan §6.3). Lag deltas are null when the exact
/// predecessor period is missing/NOT STARTED/sentinel (never differenced across a gap).
/// </summary>
public sealed record TransitionPair
{
    public required string BccId { get; init; }
    public required int PeriodId { get; init; }          // the feature period p
    public required string? Discipline { get; init; }
    public required string PackageCode { get; init; }
    /// <summary>Physical location tag (STRUCTURE, FLOORS-ALL, …). Null on workbooks without it.</summary>
    public string? ZoneArea { get; init; }

    /// <summary>Label: true iff AlertLevel(p+1) == "AMBER". (The label is the target of record.)</summary>
    public required bool Label { get; init; }

    // Level features at p
    public required double Cpi { get; init; }
    public double? Rolling3mCpi { get; init; }
    public double? Spi { get; init; }
    public double? VariancePct { get; init; }
    public double? EacVsBacRatio { get; init; }

    /// <summary>gap = Pct_Budget_Consumed − Actual_Pct_Complete, in percentage points.</summary>
    public required double Gap { get; init; }

    // Trend features (exact-predecessor only; null otherwise)
    public double? DCpi1 { get; init; }
    public double? DGap1 { get; init; }
    public double? DCpi2 { get; init; }

    // ── Peer features: how the rest of this centre's neighbourhood is performing at p ──
    //
    // Leave-one-out and aggregate: ΣEV(peers) / ΣAC(peers), never a mean of per-centre CPIs — the
    // same rule the zone cost-map and the copilot prompt already follow, so a tiny centre with a
    // wild ratio cannot outvote the money.
    //
    // NULL, never 0, when a centre has no peers. Zero would read as "the neighbourhood is in
    // catastrophic trouble" when the truth is "there is no neighbourhood to judge".

    /// <summary>Peer CPI over other live centres in the same <see cref="ZoneArea"/> at p.</summary>
    public double? PeerCpi { get; init; }
    public int PeerCount { get; init; }

    /// <summary>
    /// Peer CPI over same-zone centres of a DIFFERENT discipline — same place, different trade.
    /// On Tower X this is only defined inside FLOORS-ALL, the one zone with real trade mixing;
    /// everywhere else zone and discipline coincide and there are no cross-trade neighbours.
    /// This is the only genuinely SPATIAL peer signal the dataset can express.
    /// </summary>
    public double? PeerCpiCrossTrade { get; init; }
    public int CrossTradePeerCount { get; init; }

    // Resource shares (cumulative basis)
    public double? ShareMaterial { get; init; }
    public double? ShareManpower { get; init; }
    public double? ShareEquipment { get; init; }
    public double? ShareSubcontract { get; init; }
}
