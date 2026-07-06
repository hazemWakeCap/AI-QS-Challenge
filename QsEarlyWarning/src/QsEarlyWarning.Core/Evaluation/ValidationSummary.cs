namespace QsEarlyWarning.Core.Evaluation;

/// <summary>
/// A scorer's rolling-origin result at one k: per-fold metrics + honest aggregates. Plan §6.6.
/// Reported as fold counts + macro + fold range (no bootstrap CI — 8 folds is too few).
/// </summary>
public sealed record ScorerReport
{
    public required string ScorerLabel { get; init; }
    public required int K { get; init; }
    public required IReadOnlyList<FoldMetrics> Folds { get; init; }

    public double? MacroPrecision => Metrics.Macro(Folds.Select(f => f.Precision));
    public double? MacroRecall => Metrics.Macro(Folds.Select(f => f.Recall));
    public double? PrecisionMin => Folds.Where(f => f.Precision.HasValue).Select(f => f.Precision!.Value).DefaultIfEmpty().Min();
    public double? PrecisionMax => Folds.Where(f => f.Precision.HasValue).Select(f => f.Precision!.Value).DefaultIfEmpty().Max();
    public int TotalFalsePositives => Folds.Sum(f => f.FalsePositives);
    public double FalseAlertsPerCycle => Folds.Count == 0 ? 0 : (double)TotalFalsePositives / Folds.Count;

    /// <summary>The worst fold by precision (label + value), for surfacing tail behaviour.</summary>
    public FoldMetrics? WorstCycle =>
        Folds.Where(f => f.Precision.HasValue).OrderBy(f => f.Precision!.Value).FirstOrDefault();
}

/// <summary>
/// The frozen out-of-fold validation summary. Model-level (not per-period), labelled historical.
/// Stamped with scorer + versions so it can never be read as a different scorer's numbers.
/// </summary>
public sealed record ValidationSummary
{
    public required string Scorer { get; init; }
    public required string ScorerVersion { get; init; }
    public required string FeatureSchemaVersion { get; init; }
    public required int EvaluationOriginMin { get; init; }
    public required int EvaluationOriginMax { get; init; }
    public required int FoldCount { get; init; }
    public required int TotalTransitions { get; init; }

    /// <summary>The deployed rule, reported at each k.</summary>
    public required IReadOnlyList<ScorerReport> Rule { get; init; }

    /// <summary>CPI-native comparators, reported side by side (descriptive).</summary>
    public required IReadOnlyList<ScorerReport> CpiNative { get; init; }

    /// <summary>Optional descriptive challenger (S1), never deployed. Null when disabled.</summary>
    public IReadOnlyList<ScorerReport>? Challenger { get; init; }
}
