using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Evaluation;

/// <summary>One flagged centre graded against what actually happened next period.</summary>
public sealed record BacktestRow
{
    public required WatchlistRow Row { get; init; }
    /// <summary>The centre's ACTUAL Alert_Level at period+1, straight from the panel ("—" if absent).</summary>
    public required string ActualNextAlert { get; init; }
    /// <summary>True when the flagged centre really tipped to AMBER next period.</summary>
    public required bool Hit { get; init; }
}

/// <summary>Hindsight grade of the watchlist for one origin period.</summary>
public sealed record BacktestResult
{
    public required int Period { get; init; }
    public required int NextPeriod { get; init; }
    public required int K { get; init; }
    public required int TrainingCutoffPeriod { get; init; }
    public required int Eligible { get; init; }
    public required int Positives { get; init; }   // GREEN-at-p centres that ACTUALLY went AMBER next period
    public required int Hits { get; init; }         // of the top-k, how many tipped
    public required double? PrecisionAtK { get; init; }
    public required IReadOnlyList<BacktestRow> Rows { get; init; }
}

/// <summary>
/// Grades the deployed watchlist against reality: for a past origin period, rank the GREEN centres
/// with the leakage-safe out-of-fold model, then look up each flagged centre's ACTUAL next-period
/// Alert_Level in the panel. A hit = flagged AND next period is AMBER — precision@k made per-row and
/// human-readable (the "we called it" reveal). Only labeled origins are backtestable (the live
/// forecast period has no successor to grade against).
/// </summary>
public sealed class WatchlistBacktestService
{
    private readonly WatchlistScoringService _scoring;

    public WatchlistBacktestService(WatchlistScoringService scoring) => _scoring = scoring;

    /// <summary>True when <paramref name="period"/> has both an OOF artifact and a real next period.</summary>
    public static bool IsBacktestable(ReportingOrigins origins, int period)
        => period >= origins.FirstOrigin && period <= origins.LastLabeledPeriod;

    /// <summary>Grades period p, or null if p is not a labeled origin (e.g. the live forecast period).</summary>
    public BacktestResult? Evaluate(IReadOnlyList<CostCentrePeriod> panel, int period, int k, TrainedModel model)
    {
        if (!IsBacktestable(model.Origins, period))
            return null;

        var score = _scoring.ScorePeriod(panel, period, model);
        if (score.Status != ScoreStatus.Ok)
            return null;

        int next = period + 1;
        var actualNext = panel
            .Where(r => r.PeriodId == next)
            .GroupBy(r => r.BccId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().AlertLevel, StringComparer.Ordinal);

        static bool IsAmber(string? a) => string.Equals(a, "AMBER", StringComparison.OrdinalIgnoreCase);

        var topK = score.Rows.Take(k).Select(r =>
        {
            var actual = actualNext.TryGetValue(r.BccId, out var a) ? a : null;
            return new BacktestRow { Row = r, ActualNextAlert = actual ?? "—", Hit = IsAmber(actual) };
        }).ToList();

        int eligible = score.Rows.Count;
        int hits = topK.Count(x => x.Hit);
        int positives = score.Rows.Count(r => actualNext.TryGetValue(r.BccId, out var a) && IsAmber(a));
        // precision@k = TP / min(k, eligible), matching Metrics.PrecisionAtK.
        double? precision = eligible == 0 ? null : (double)hits / Math.Min(k, eligible);

        return new BacktestResult
        {
            Period = period,
            NextPeriod = next,
            K = k,
            TrainingCutoffPeriod = score.TrainingCutoffPeriod,
            Eligible = eligible,
            Positives = positives,
            Hits = hits,
            PrecisionAtK = precision,
            Rows = topK,
        };
    }
}
