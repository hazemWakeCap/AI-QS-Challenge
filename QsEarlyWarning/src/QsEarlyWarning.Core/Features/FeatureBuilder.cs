using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Features;

/// <summary>Outcome of pairing a period, with excluded-count bookkeeping. Plan §6.3.</summary>
public sealed record PairingResult
{
    public required IReadOnlyList<TransitionPair> Pairs { get; init; }
    /// <summary>GREEN-at-p rows whose successor was NOT STARTED / missing / invalid — excluded, counted.</summary>
    public required int ExcludedCount { get; init; }
}

/// <summary>
/// Builds GREEN→(AMBER?) transition pairs and engineers features. Plan §6.3.
///
/// Adjacency is EXPLICIT: a pair exists only where next.PeriodId == current.PeriodId + 1.
/// Lag deltas require exact p−1 / p−2 predecessors; otherwise null (never differenced across a gap).
///
/// Eligible-population + label contract:
///   - current row eligible iff GREEN and scoreable (finite CPI/gap inputs)
///   - Label = successor is AMBER
///   - successor GREEN/CLOSED → negative (kept, y=false)
///   - successor NOT STARTED / missing / invalid → excluded and counted
/// </summary>
public sealed class FeatureBuilder
{
    /// <summary>Builds pairs for a single feature period p across all centres.</summary>
    public PairingResult BuildPairsForPeriod(IReadOnlyList<CostCentrePeriod> panel, int p)
    {
        var byBcc = panel
            .GroupBy(r => r.BccId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.PeriodId), StringComparer.Ordinal);

        var pairs = new List<TransitionPair>();
        var excluded = 0;

        foreach (var (_, periods) in byBcc)
        {
            if (!periods.TryGetValue(p, out var cur) || !cur.IsScoreableGreen)
                continue;

            // Explicit successor at exactly p+1.
            if (!periods.TryGetValue(p + 1, out var next) || next.AlertLevel is null)
            {
                excluded++;
                continue;
            }

            var succ = next.AlertLevel;
            bool isAmber = Eq(succ, "AMBER");
            bool isNegative = Eq(succ, "GREEN") || Eq(succ, "CLOSED");

            if (!isAmber && !isNegative)
            {
                // NOT STARTED / anything else → excluded and counted.
                excluded++;
                continue;
            }

            pairs.Add(Engineer(cur, isAmber, periods));
        }

        return new PairingResult { Pairs = pairs, ExcludedCount = excluded };
    }

    /// <summary>
    /// Engineers feature rows for scoring a period's GREEN-at-p population — WITHOUT a successor.
    /// Used to produce a watchlist (the label is unknown/irrelevant at scoring time, so the
    /// forecast period 12, which has no successor, still yields rows). Plan §6.7.
    /// </summary>
    public IReadOnlyList<TransitionPair> BuildScoringRows(IReadOnlyList<CostCentrePeriod> panel, int p)
    {
        var byBcc = panel
            .GroupBy(r => r.BccId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.PeriodId), StringComparer.Ordinal);

        var rows = new List<TransitionPair>();
        foreach (var (_, periods) in byBcc)
        {
            if (!periods.TryGetValue(p, out var cur) || !cur.IsScoreableGreen)
                continue;
            rows.Add(Engineer(cur, label: false, periods)); // label unused for scoring
        }
        return rows;
    }

    /// <summary>Builds pairs for all feature periods in [minP, maxP].</summary>
    public PairingResult BuildPairs(IReadOnlyList<CostCentrePeriod> panel, int minP, int maxP)
    {
        var all = new List<TransitionPair>();
        var excluded = 0;
        for (int p = minP; p <= maxP; p++)
        {
            var r = BuildPairsForPeriod(panel, p);
            all.AddRange(r.Pairs);
            excluded += r.ExcludedCount;
        }
        return new PairingResult { Pairs = all, ExcludedCount = excluded };
    }

    private static TransitionPair Engineer(CostCentrePeriod cur, bool label, Dictionary<int, CostCentrePeriod> periods)
    {
        var p = cur.PeriodId;
        double gap = cur.Gap!.Value;

        // Lag deltas: exact-predecessor only.
        double? dCpi1 = Delta(periods, p, p - 1, r => r.Cpi);
        double? dGap1 = Delta(periods, p, p - 1, r => r.Gap);
        double? dCpi2 = Delta(periods, p, p - 2, r => r.Cpi);

        double? acCum = cur.AcCumulative;
        double? Share(double? part) =>
            part is double v && acCum is double d && d != 0 ? v / d : null;

        return new TransitionPair
        {
            BccId = cur.BccId,
            PeriodId = p,
            Discipline = cur.Discipline,
            PackageCode = cur.PackageCode,
            Label = label,
            Cpi = cur.Cpi!.Value,
            Rolling3mCpi = cur.Rolling3mCpi,
            Spi = cur.Spi,
            VariancePct = cur.VariancePct,
            EacVsBacRatio = cur.EacVsBacRatio,
            Gap = gap,
            DCpi1 = dCpi1,
            DGap1 = dGap1,
            DCpi2 = dCpi2,
            ShareMaterial = Share(cur.AcMaterial),
            ShareManpower = Share(cur.AcManpower),
            ShareEquipment = Share(cur.AcEquipment),
            ShareSubcontract = Share(cur.AcSubcontract),
        };
    }

    /// <summary>
    /// value(p) − value(pred) only when an EXACT predecessor period exists, is not NOT STARTED,
    /// and both values are present. Otherwise null — never differenced across a gap.
    /// </summary>
    private static double? Delta(
        Dictionary<int, CostCentrePeriod> periods, int p, int predPeriod, Func<CostCentrePeriod, double?> sel)
    {
        if (predPeriod < 1) return null;
        if (!periods.TryGetValue(predPeriod, out var pred)) return null;
        if (Eq(pred.AlertLevel, "NOT STARTED") || pred.AlertLevel is null) return null;
        var now = sel(periods[p]);
        var before = sel(pred);
        return now is double a && before is double b ? a - b : null;
    }

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
