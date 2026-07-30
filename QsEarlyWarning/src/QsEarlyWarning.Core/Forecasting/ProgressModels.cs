namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// Physical-progress projection past the last reported period — the shapes the 4D build sequence
/// consumes to keep rising after the workbook stops.
///
/// <b>This forecasts progress, not money.</b> The idea-2 forecaster in this same namespace projects
/// incremental spend (ΔAC); it says nothing about how much of a cost centre physically stands. The 4D
/// sequence reveals geometry from <c>Actual_Pct_Complete</c>, so extending it past the origin needs a
/// projection of that percentage and nothing else. No EV, AC, CPI or EAC is derived here, and none
/// should be derived downstream from these numbers: a physical-progress projection is not a licence to
/// invent cost figures.
/// </summary>
public enum ProgressTier
{
    /// <summary>Reported in the workbook. Not a projection at all.</summary>
    Measured,

    /// <summary>Within the back-tested horizon (h ≤ <see cref="ProgressConfig.BacktestedHorizon"/>):
    /// a published error bar stands behind it.</summary>
    Forecast,

    /// <summary>Past the back-tested horizon. The same arithmetic, but no measurement of how well it
    /// does this far out — because the workbook is not long enough to contain one.</summary>
    Extrapolated,
}

/// <summary>Frozen configuration for the progress projection — declared once, never tuned on results.</summary>
public static class ProgressConfig
{
    /// <summary>Periods averaged to form the pace. Matches <see cref="IncrementHelper.RecentQtyPace"/>'s
    /// window, so "recent" means the same thing everywhere in this namespace.</summary>
    public const int PaceWindow = 3;

    /// <summary>Horizons the back-test scores, and therefore the furthest a band is measured rather
    /// than scaled. Deliberately the same {1,2,3} as <see cref="ForecastConfig.Horizons"/>.</summary>
    public const int BacktestedHorizon = 3;

    /// <summary>
    /// Hard cap on how far past the origin a projection will run.
    ///
    /// Without it, a centre creeping at 0.3 pp/period asks for a 297-period timeline and the caller
    /// dutifully builds one. Two years of monthly periods is already far beyond anything this method
    /// can defend; the cap is a backstop against nonsense, not a claim that 24 is meaningful.
    /// </summary>
    public const int MaxHorizon = 24;

    /// <summary>Nominal interval, matching <see cref="ForecastConfig.Alpha"/> — an 80% band.</summary>
    public const double Alpha = 0.20;
}

/// <summary>One period's projected physical progress for one centre, as percent complete (0..100).</summary>
public sealed record ProgressPoint(int Period, double P50Pct, double? P10Pct, double? P90Pct, ProgressTier Tier);

public sealed record CentreProgressForecast
{
    public required string BccId { get; init; }
    public required int OriginPeriod { get; init; }
    public required double ActualPctAtOrigin { get; init; }

    /// <summary>Percentage points of progress per period, averaged over the pace window and clamped at ≥ 0.</summary>
    public required double PacePctPerPeriod { get; init; }

    /// <summary>First period the centre reaches 100% at this pace. Null when stalled.</summary>
    public int? ProjectedFinishPeriod { get; init; }

    /// <summary>No forward progress to project — either no adjacent history, or a pace of zero or less.
    /// Such a centre never completes, and the projection says so rather than inventing a finish.</summary>
    public required bool Stalled { get; init; }

    /// <summary>
    /// The centre's alert level at the origin, carried forward unchanged for every projected period.
    ///
    /// An alert is a verdict on cost performance, and no verdict exists for a period that has not
    /// happened. Carrying the last one forward is the same assumption the pace already makes — that
    /// the current regime continues — so it asserts nothing extra. Inventing a new colour for
    /// "forecast" would assert less than the data supports; the 3D view distinguishes projected work
    /// by opacity instead.
    /// </summary>
    public string? AlertAtOrigin { get; init; }

    public required IReadOnlyList<ProgressPoint> Points { get; init; }
}

/// <summary>Per-horizon back-test result for one predictor, in percentage points of progress.</summary>
public sealed record ProgressHorizonMetric(string Predictor, int Horizon, int N, double MaePp, double? CoverageP10P90);

/// <summary>Empirical residual quantiles (truth − prediction, percentage points) that become the band.</summary>
public sealed record ProgressResidualQuantiles(int Horizon, double P10, double P90, int N);

public sealed record ProgressValidationSummary
{
    public required string Provenance { get; init; }
    public required int OriginMin { get; init; }
    public required int OriginMax { get; init; }
    public required int Centres { get; init; }
    public required IReadOnlyList<ProgressHorizonMetric> Metrics { get; init; }
    public required IReadOnlyList<ProgressResidualQuantiles> Bands { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
}

public sealed record ProgressForecast
{
    public required int OriginPeriod { get; init; }

    /// <summary>Last period projected in this response.</summary>
    public required int HorizonPeriod { get; init; }

    /// <summary>Last period covered by a measured error bar (origin + <see cref="ProgressConfig.BacktestedHorizon"/>).
    /// Past it the projection continues but its accuracy is unmeasured.</summary>
    public required int BacktestedThroughPeriod { get; init; }

    /// <summary>The period the last of the requested centres tops out, capped at the max horizon —
    /// what a caller should use as a timeline ceiling. Equals the origin when every centre is stalled.</summary>
    public required int SuggestedHorizonPeriod { get; init; }

    public required string Method { get; init; }
    public required IReadOnlyList<CentreProgressForecast> Centres { get; init; }
    public required ProgressValidationSummary Validation { get; init; }
}
