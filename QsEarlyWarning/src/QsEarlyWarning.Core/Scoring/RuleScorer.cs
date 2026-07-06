using QsEarlyWarning.Core.Features;

namespace QsEarlyWarning.Core.Scoring;

/// <summary>
/// The frozen RuleRiskScore@v1 (plan §6.4). The deployed, predeclared scorer.
///
///   RuleRiskScore = w_gap · clamp01((gap − x*) / gap_scale) + w_cpi · cpiProximity
///   cpiProximity  = clamp01(1 − (Cpi − 0.95) / cpi_band)     // proximity-from-ABOVE
///
/// gap, x*, gap_scale are in percentage points. Cpi/cpi_band are unitless ratios.
/// cpiProximity is maximal (1) at the 0.95 boundary and decays to 0 at/above 0.95+cpi_band —
/// the correct direction for a GREEN population (Cpi ≥ 0.95), where a "distance below 0.95"
/// term would be identically zero.
/// </summary>
public static class RuleScorer
{
    public static double Clamp01(double z) => Math.Min(1.0, Math.Max(0.0, z));

    /// <summary>The CPI component in isolation (guarded so tests can assert its shape).</summary>
    public static double CpiProximity(double cpi, double cpiBand)
        => Clamp01(1.0 - (cpi - 0.95) / cpiBand);

    public static double Score(TransitionPair pair, RuleArtifact a)
        => Score(pair.Gap, pair.Cpi, a);

    public static double Score(double gap, double cpi, RuleArtifact a)
    {
        double gapComponent = Clamp01((gap - a.XStar) / a.GapScale);
        double cpiComponent = CpiProximity(cpi, a.CpiBand);
        return a.WGap * gapComponent + a.WCpi * cpiComponent;
    }

    /// <summary>2–3 deterministic reason codes for a row (contextual, not causal). Plan §6.7.</summary>
    public static IReadOnlyList<string> RiskIndicators(TransitionPair p, RuleArtifact a)
    {
        var items = new List<(double weight, string text)>();

        if (p.Gap > a.XStar)
            items.Add((p.Gap, $"spending {p.Gap:0.0}pp ahead of progress"));

        double cpiHead = p.Cpi - 0.95;
        if (cpiHead <= a.CpiBand)
            items.Add((1.0 - cpiHead / a.CpiBand, $"CPI {p.Cpi:0.000} — close to the 0.95 line"));

        if (p.DCpi1 is double d1 && d1 < 0)
            items.Add((-d1, $"CPI down {(-d1):0.000} since last period"));

        if (items.Count == 0)
            items.Add((0, "no dominant driver — ranked by combined score"));

        return items
            .OrderByDescending(x => x.weight)
            .Take(3)
            .Select(x => x.text)
            .ToList();
    }
}
