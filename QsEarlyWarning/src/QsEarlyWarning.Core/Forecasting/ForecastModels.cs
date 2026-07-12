namespace QsEarlyWarning.Core.Forecasting;

/// <summary>Frozen configuration — declared once, never estimated per fold, never tuned on results.</summary>
public static class ForecastConfig
{
    public const double Alpha = 0.20;                 // nominal 80% prediction interval
    public const double Lambda = 1.0;                 // ridge penalty on standardized features
    public static readonly double[] RidgeFloorRetries = { 10.0, 100.0 };
    public static readonly int[] Horizons = { 1, 2, 3 };
    public static readonly double[] ProgressBinEdges = { 0, 10, 25, 50, 100 };  // % complete
    public const int MinCount = 60;                   // min calibration residuals before a bin's own quantiles are trusted
    public const double ProgressGatePct = 10.0;       // below this a centre is "too early"
    public const double ClaimBandPct = 40.0;          // early-progress band where the win is claimed
    public const int KFolds = 10;                     // hash(BccId) mod 10
    public const int MinTrainRows = 40;
    public const int MinCalRows = 30;
    public const int MonteCarloDraws = 2000;

    /// <summary>Stable seedless FNV-1a hash of BccId → fold id in [0,KFolds). Identical across runs.</summary>
    public static int Fold(string bccId)
    {
        uint h = 2166136261u;
        foreach (var c in bccId) { h ^= c; h *= 16777619u; }
        return (int)(h % KFolds);
    }

    /// <summary>Back-test split: buckets 0–6 → proper-training, 7–9 → calibration (a centre is on one side always).</summary>
    public static bool IsCalibration(string bccId) => Fold(bccId) >= 7;

    public static int ProgressBin(double progressPct)
    {
        for (int i = 1; i < ProgressBinEdges.Length; i++)
            if (progressPct < ProgressBinEdges[i]) return i - 1;
        return ProgressBinEdges.Length - 2;
    }
}

public enum TrustBadge { Validatable, TooEarly, InsufficientCalibration }

/// <summary>One engineered training/serving row: features as-of the feature period, label at the target period.</summary>
public sealed record IncrementSample
{
    public required string BccId { get; init; }
    public required int FeaturePeriod { get; init; }
    public required int Horizon { get; init; }
    public required int TargetPeriod { get; init; }      // FeaturePeriod + Horizon
    public required double ProgressPct { get; init; }    // at FeaturePeriod
    public required double Bac { get; init; }
    public required double[] Features { get; init; }
    public required bool[] Missing { get; init; }        // was-missing indicators (parallel to Features)
    public double? Label { get; init; }                  // realized increment at TargetPeriod (null if not present)
    public int Fold { get; init; }
}

/// <summary>One horizon's forecast increment as a nominal 80% interval (null endpoints = unbounded/unavailable).</summary>
public sealed record HorizonBand(int Horizon, double P50, double? P10, double? P90, bool Available);

/// <summary>A point on the cumulative cost cone (reconstructed from increments + joint residual simulation).</summary>
public sealed record ConePoint(int Period, double P50, double? P10, double? P90);

public sealed record CentreForecast
{
    public required string BccId { get; init; }
    public required int OriginPeriod { get; init; }
    public required double ProgressPct { get; init; }
    public required double Bac { get; init; }
    public required double AcAtOrigin { get; init; }
    public required TrustBadge Trust { get; init; }
    public required IReadOnlyList<HorizonBand> Increments { get; init; }
    public required IReadOnlyList<ConePoint> CumulativeCone { get; init; }   // may be empty if joint calibration insufficient
    public bool CumulativeConeAvailable { get; init; }
    /// <summary>Directional, NOT validated: BAC/CPI-style extrapolation to 100% for the subordinated overlay.</summary>
    public double? DirectionalFinalCost { get; init; }
}

public sealed record ProjectSpendScenario(
    int OriginPeriod, double P10, double P50, double P90, int Centres, int Draws);

/// <summary>One future period's repriced spend under a unit-rate scenario.</summary>
public sealed record ScenarioPeriodSpend(int Period, double Qty, double Spend, double UnitRate);

/// <summary>
/// A deterministic unit-rate "what-if": the centre's remaining quantity repriced at a user-supplied
/// AED/unit rate, flowing at the recent physical pace. NOT a validated forecast — every figure is a
/// direct arithmetic consequence of the stated assumption, surfaced so the copilot can narrate it as
/// a scenario and contrast it with the current realized rate.
/// </summary>
public sealed record ScenarioForecast
{
    public required bool Available { get; init; }
    public string? UnavailableReason { get; init; }

    public required string BccId { get; init; }
    public required int OriginPeriod { get; init; }
    public string? Unit { get; init; }

    // Assumption (echoed back)
    public required double NewUnitRate { get; init; }
    public required int EffectiveFromPeriod { get; init; }

    // Baseline the scenario is measured against
    public double BudgetQty { get; init; }
    public double RemainingQty { get; init; }
    public double? PlannedUnitRate { get; init; }        // = BAC / BudgetQty
    public double? CurrentRealizedRate { get; init; }    // = AC / EarnedQty
    public double RecentQtyPacePerPeriod { get; init; }

    // Scenario projection
    public required IReadOnlyList<ScenarioPeriodSpend> Increments { get; init; }
    public double ScenarioCostToComplete { get; init; }
    public double ScenarioFinalCost { get; init; }       // = AC(origin) + costToComplete
    public double? ScenarioVac { get; init; }            // = BAC − finalCost

    public static ScenarioForecast Unavailable(string bccId, int origin, double rate, int effectiveFrom, string reason) =>
        new()
        {
            Available = false, UnavailableReason = reason,
            BccId = bccId, OriginPeriod = origin, NewUnitRate = rate, EffectiveFromPeriod = effectiveFrom,
            Increments = Array.Empty<ScenarioPeriodSpend>(),
        };
}

/// <summary>Per-horizon back-test metrics for one predictor (the model or a baseline), on identical eligible rows.</summary>
public sealed record HorizonMetric(
    string Predictor, int Horizon, int N,
    double MaePctOfBac, double Wape, double? CoverageP10P90,
    double? CoverageLow, double? CoverageHigh,   // Wilson band (model only; baselines have no interval)
    int FallbackCount);

public sealed record ForecastValidationSummary
{
    public required string Provenance { get; init; }
    public required int OriginMin { get; init; }
    public required int OriginMax { get; init; }
    public required int FoldsEvaluated { get; init; }
    public required int FoldsSkipped { get; init; }
    public required IReadOnlyList<HorizonMetric> Overall { get; init; }     // model + 4 baselines × horizons
    public required IReadOnlyList<HorizonMetric> EarlyBand { get; init; }   // progress < ClaimBandPct
    public required IReadOnlyList<string> Notes { get; init; }
}
