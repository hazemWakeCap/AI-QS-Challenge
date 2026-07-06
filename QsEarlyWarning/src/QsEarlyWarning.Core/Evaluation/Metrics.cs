namespace QsEarlyWarning.Core.Evaluation;

/// <summary>A scored candidate for ranking: deterministic tie-break is (score desc, BccId asc).</summary>
public readonly record struct ScoredCandidate(string BccId, double Score, bool Label);

/// <summary>
/// Ranking/alert metrics under the single top-k contract. Plan §6.6.
///   precision@k = TP / min(k, eligibleCount); alert set = the period's top-k.
///   Zero-positive folds → recall = null (N/A, excluded from macro-recall).
/// </summary>
public static class Metrics
{
    /// <summary>Ranks candidates (score desc, BccId asc) and returns the top-k as the alert set.</summary>
    public static IReadOnlyList<ScoredCandidate> TopK(IEnumerable<ScoredCandidate> candidates, int k)
        => candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.BccId, StringComparer.Ordinal)
            .Take(k)
            .ToList();

    /// <summary>precision@k = TP / min(k, eligibleCount). Null if no eligible candidates.</summary>
    public static double? PrecisionAtK(IReadOnlyList<ScoredCandidate> candidates, int k)
    {
        int eligible = candidates.Count;
        if (eligible == 0) return null;
        int kEff = Math.Min(k, eligible);
        int tp = TopK(candidates, k).Count(c => c.Label);
        return (double)tp / kEff;
    }

    /// <summary>Full per-fold counts under the top-k alert set.</summary>
    public static FoldMetrics ForFold(int periodId, IReadOnlyList<ScoredCandidate> candidates, int k)
    {
        int eligible = candidates.Count;
        int positives = candidates.Count(c => c.Label);
        var alerted = TopK(candidates, k);
        int tp = alerted.Count(c => c.Label);
        int fp = alerted.Count - tp;
        int fn = positives - tp;
        int kEff = Math.Min(k, Math.Max(eligible, 0));

        double? precision = eligible == 0 ? null : (double)tp / Math.Max(kEff, 1);
        double? recall = positives == 0 ? null : (double)tp / positives; // N/A when zero-positive

        return new FoldMetrics
        {
            PeriodId = periodId,
            K = k,
            KEffective = kEff,
            Eligible = eligible,
            Positives = positives,
            TruePositives = tp,
            FalsePositives = fp,
            FalseNegatives = fn,
            Precision = precision,
            Recall = recall,
        };
    }

    /// <summary>Macro mean over folds, skipping null values (e.g. zero-positive recall).</summary>
    public static double? Macro(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.Average();
    }
}

public sealed record FoldMetrics
{
    public required int PeriodId { get; init; }
    public required int K { get; init; }
    public required int KEffective { get; init; }
    public required int Eligible { get; init; }
    public required int Positives { get; init; }
    public required int TruePositives { get; init; }
    public required int FalsePositives { get; init; }
    public required int FalseNegatives { get; init; }
    public required double? Precision { get; init; }
    public required double? Recall { get; init; }
}
