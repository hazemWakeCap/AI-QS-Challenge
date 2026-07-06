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

    // Resource shares (cumulative basis)
    public double? ShareMaterial { get; init; }
    public double? ShareManpower { get; init; }
    public double? ShareEquipment { get; init; }
    public double? ShareSubcontract { get; init; }
}
