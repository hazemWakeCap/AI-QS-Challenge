using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// The forecaster's own exact-predecessor increment/lag helper (NOT the private, alert-coupled
/// <c>FeatureBuilder.Delta</c>). Differences are formed only across an exactly-adjacent present
/// predecessor period — never across a gap — so cumulative-to-increment conversion is leakage-safe.
/// </summary>
public static class IncrementHelper
{
    private static double? Diff(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int k, Func<CostCentrePeriod, double?> sel)
    {
        if (!byPeriod.TryGetValue(k, out var cur) || !byPeriod.TryGetValue(k - 1, out var prev)) return null;
        return sel(cur) is double a && double.IsFinite(a) && sel(prev) is double b && double.IsFinite(b) ? a - b : null;
    }

    /// <summary>Actual-cost increment ΔAC(k) = AcCumulative(k) − AcCumulative(k−1).</summary>
    public static double? AcInc(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int k)
        => Diff(byPeriod, k, r => r.AcCumulative);

    /// <summary>Planned-value increment ΔPV(k) = PvAed(k) − PvAed(k−1).</summary>
    public static double? PvInc(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int k)
        => Diff(byPeriod, k, r => r.PvAed);

    /// <summary>Earned-value increment ΔEV(k).</summary>
    public static double? EvInc(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int k)
        => Diff(byPeriod, k, r => r.EvAed);

    /// <summary>Earned-quantity increment ΔEarnedQty(k) = EarnedQtyCumul(k) − EarnedQtyCumul(k−1).</summary>
    public static double? QtyInc(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int k)
        => Diff(byPeriod, k, r => r.EarnedQtyCumul);

    /// <summary>Recent physical pace: mean of the present earned-quantity increments over the ≤3
    /// periods ending at k (units built per period). Null if no adjacent increment is present.</summary>
    public static double? RecentQtyPace(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int k)
    {
        double s = 0; int n = 0;
        for (int j = k; j > k - 3; j--)
            if (QtyInc(byPeriod, j) is double q) { s += q; n++; }
        return n > 0 ? s / n : null;
    }

    /// <summary>Rolling CPI over the ≤3 present periods ending at k (current period included):
    /// ΣΔEV ÷ ΣΔAC (sum-of-increments, not a mean of per-period ratios). Null if the AC denominator is 0/absent.</summary>
    public static double? RollCpi(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int k)
    {
        double ev = 0, ac = 0; int seen = 0;
        for (int j = k; j > k - 3; j--)
        {
            var de = EvInc(byPeriod, j); var da = AcInc(byPeriod, j);
            if (de is double e && da is double a) { ev += e; ac += a; seen++; }
        }
        return seen > 0 && Math.Abs(ac) > 1e-9 ? ev / ac : null;
    }

    /// <summary>Mean of the present AC increments over the ≤3 periods ending at k (recent run-rate).</summary>
    public static double? RunRate(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int k)
    {
        double s = 0; int n = 0;
        for (int j = k; j > k - 3; j--)
            if (AcInc(byPeriod, j) is double a) { s += a; n++; }
        return n > 0 ? s / n : null;
    }
}
