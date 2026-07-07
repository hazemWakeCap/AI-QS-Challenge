using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// Builds per-centre, per-horizon increment samples. Features are computed strictly from periods ≤ k
/// (the feature period); the label is the realized single-period increment ΔAC(k+h) at the TARGET
/// period k+h. `EacAed`/`VacAed`/`EacVsBacRatio` are never used (they equal BAC/CPI — they'd leak the
/// baseline). The planned-target increment ΔPV(k+h) is a valid feature: the plan curve is static
/// (known as-of origin), so it carries no schedule-vintage leakage on this project.
/// </summary>
public sealed class ForecastFeatureBuilder
{
    public const int FeatureCount = 6;
    public static readonly string[] FeatureNames =
        { "plannedTargetInc", "recentInc", "prevInc", "rollCpi", "progressFrac", "runRate" };

    public IReadOnlyList<IncrementSample> BuildAll(IReadOnlyList<CostCentrePeriod> panel)
    {
        var samples = new List<IncrementSample>();
        foreach (var g in panel.GroupBy(r => r.BccId, StringComparer.Ordinal))
        {
            var byPeriod = g.ToDictionary(r => r.PeriodId);
            double bac = g.Select(r => r.BacAed).FirstOrDefault(v => v is double d && double.IsFinite(d)) ?? 0.0;
            if (bac <= 0) continue;   // cannot normalize / no budget
            int fold = ForecastConfig.Fold(g.Key);

            foreach (var k in byPeriod.Keys.OrderBy(x => x))
            {
                var cur = byPeriod[k];
                double progress = cur.ActualPctComplete is double p && double.IsFinite(p) ? p : 0.0;

                foreach (var h in ForecastConfig.Horizons)
                {
                    int target = k + h;
                    // Model in BAC-fraction space (ΔAC/BAC) so centres of vastly different budget share one
                    // scale-free model; predictions are converted back to cost via × BAC downstream. Still a
                    // cost-space forecast (never divides by CPI). Planned-target increment ΔPV(k+h) is known
                    // as-of k (static plan). NaN → ridge-imputed + was-missing flag. rollCpi/progress are ratios.
                    double? pvIncFrac = IncrementHelper.PvInc(byPeriod, target) is double pv ? pv / bac : null;
                    var features = new[]
                    {
                        pvIncFrac ?? double.NaN,
                        IncrementHelper.AcInc(byPeriod, k) is double a0 ? a0 / bac : double.NaN,
                        IncrementHelper.AcInc(byPeriod, k - 1) is double a1 ? a1 / bac : double.NaN,
                        IncrementHelper.RollCpi(byPeriod, k) ?? double.NaN,
                        progress / 100.0,
                        IncrementHelper.RunRate(byPeriod, k) is double rr ? rr / bac : double.NaN,
                    };
                    var missing = features.Select(f => !double.IsFinite(f)).ToArray();
                    double? labelFrac = IncrementHelper.AcInc(byPeriod, target) is double lb ? lb / bac : null;

                    samples.Add(new IncrementSample
                    {
                        BccId = g.Key, FeaturePeriod = k, Horizon = h, TargetPeriod = target,
                        ProgressPct = progress, Bac = bac,
                        Features = features, Missing = missing,
                        Label = labelFrac is double lf && double.IsFinite(lf) ? lf : null,   // fraction of BAC
                        Fold = fold,
                    });
                }
            }
        }
        return samples;
    }

    /// <summary>Design row = features (NaN → ridge-imputed) concatenated with was-missing indicators (0/1).</summary>
    public static double[] Design(IncrementSample s)
    {
        var d = new double[FeatureCount * 2];
        for (int j = 0; j < FeatureCount; j++) { d[j] = s.Features[j]; d[FeatureCount + j] = s.Missing[j] ? 1.0 : 0.0; }
        return d;
    }
}
