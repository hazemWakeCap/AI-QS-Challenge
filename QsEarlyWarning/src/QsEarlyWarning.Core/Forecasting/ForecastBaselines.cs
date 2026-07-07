namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// The four baselines the model must be compared against (idea §baselines). Each maps an
/// <see cref="IncrementSample"/> to a predicted single-period increment at the target, plus a flag
/// when a fallback path was taken (surfaced in the back-test). Features order matches
/// <see cref="ForecastFeatureBuilder.FeatureNames"/>: [plannedTargetInc, recentInc, prevInc, rollCpi,
/// progressFrac, runRate].
/// </summary>
public static class ForecastBaselines
{
    public static readonly IReadOnlyList<(string Label, Func<IncrementSample, (double Pred, bool Fallback)>)> All = new (string, Func<IncrementSample, (double, bool)>)[]
    {
        ("zero-increment", _ => (0.0, false)),
        ("planned-spend", s => (Fin(s.Features[0]) ?? 0.0, !double.IsFinite(s.Features[0]))),
        ("recent-run-rate", s => (Fin(s.Features[5]) ?? Fin(s.Features[1]) ?? 0.0, false)),
        ("cpi-based", CpiBased),
    };

    // Cost-to-do = planned-value-to-do ÷ efficiency: ΔPV(k+h) ÷ CPI (CPI = ΣΔEV/ΣΔAC). Divide, not multiply,
    // and NOT cumulative BAC/CPI. Fall back to planned-spend when CPI is 0/undefined/non-finite/≤0.
    private static (double, bool) CpiBased(IncrementSample s)
    {
        double plan = Fin(s.Features[0]) ?? 0.0;
        double? cpi = Fin(s.Features[3]);
        if (cpi is double c && c > 1e-6 && double.IsFinite(c)) return (plan / c, false);
        return (plan, true);   // fallback to planned-spend, flagged
    }

    private static double? Fin(double v) => double.IsFinite(v) ? v : null;
}
