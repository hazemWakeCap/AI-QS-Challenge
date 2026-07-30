using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// Rolling-origin back-test of the physical-progress projection, and the source of its bands.
///
/// For every origin <c>o</c> in the derived schedule and every horizon <c>h</c>, each centre's progress
/// at <c>o+h</c> is predicted using only rows at or before <c>o</c>, and scored against what the
/// workbook actually reported. Four predictors are scored on identical eligible rows; the method does
/// not have to win, it has to be measured.
///
/// <b>Why the bands come from here and are not constants.</b> The interval a projection carries is the
/// spread of the errors this method actually made on this project. Hard-coding "±4 pp" would be a
/// number with no provenance that silently stops being true on the next dataset. Computing it at
/// snapshot build means the band is always the band this data earned.
///
/// <b>Why there is nothing past h=3.</b> Scoring a horizon needs a period to score it against. With
/// twelve reported periods the longest honest horizon is short, so the projection is measured to +3
/// and merely continues after that — which the tier on every point past it says out loud.
/// </summary>
public sealed class ProgressBacktest
{
    /// <summary>Predictors compared on identical rows. Each maps (history at o, origin, horizon) → predicted
    /// percent complete at o+h, or null when it cannot form a prediction.</summary>
    private static readonly (string Label, Func<IReadOnlyDictionary<int, CostCentrePeriod>, int, int, double?> Fn)[] Predictors =
    {
        // The deployable method: continue the recent measured pace.
        ("pace", (byPeriod, o, h) => Project(byPeriod, o, h, IncrementHelper.RecentProgressPace(byPeriod, o))),

        // Assume no further work happens. The floor any method must beat to be worth having.
        ("hold", (byPeriod, o, _) => At(byPeriod, o)),

        // Continue the plan's own recent pace — "the schedule, as recently drawn".
        ("plan-pace", (byPeriod, o, h) => Project(byPeriod, o, h, IncrementHelper.RecentPlanPace(byPeriod, o))),

        // The plan's pace discounted by how far behind schedule the centre already is.
        ("plan-pace-spi", (byPeriod, o, h) =>
        {
            var pace = IncrementHelper.RecentPlanPace(byPeriod, o);
            if (pace is not double p) return null;
            double spi = byPeriod.TryGetValue(o, out var r) && r.Spi is double s && double.IsFinite(s) && s > 0.2 && s < 3.0 ? s : 1.0;
            return Project(byPeriod, o, h, p * spi);
        }),
    };

    public ProgressValidationSummary Evaluate(IReadOnlyList<CostCentrePeriod> panel, ReportingOrigins origins)
    {
        var byCentre = panel
            .GroupBy(p => p.BccId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<int, CostCentrePeriod>)g
                .GroupBy(p => p.PeriodId)
                .ToDictionary(x => x.Key, x => x.First()), StringComparer.OrdinalIgnoreCase);

        var horizons = Enumerable.Range(1, ProgressConfig.BacktestedHorizon).ToArray();

        // (predictor, horizon) → absolute errors, in percentage points.
        var errors = new Dictionary<(string, int), List<double>>();
        // Signed residuals for the deployable method only — these become the bands.
        var residuals = new Dictionary<int, List<double>>();
        foreach (var (label, _) in Predictors) foreach (var h in horizons) errors[(label, h)] = new();
        foreach (var h in horizons) residuals[h] = new();

        int oMin = int.MaxValue, oMax = int.MinValue;

        foreach (var byPeriod in byCentre.Values)
        {
            // Origins are bounded by LastLabeledPeriod, not ForecastPeriod: an origin needs a present
            // successor to be scoreable at all.
            for (int o = origins.FirstOrigin; o <= origins.LastLabeledPeriod; o++)
            {
                if (At(byPeriod, o) is null) continue;

                foreach (var h in horizons)
                {
                    if (At(byPeriod, o + h) is not double truth) continue;

                    bool scored = false;
                    foreach (var (label, fn) in Predictors)
                    {
                        if (fn(byPeriod, o, h) is not double pred) continue;
                        errors[(label, h)].Add(Math.Abs(pred - truth));
                        if (label == "pace") { residuals[h].Add(truth - pred); scored = true; }
                    }

                    if (scored) { oMin = Math.Min(oMin, o); oMax = Math.Max(oMax, o); }
                }
            }
        }

        var bands = horizons
            .Select(h => new ProgressResidualQuantiles(h,
                Quantile(residuals[h], ProgressConfig.Alpha / 2),
                Quantile(residuals[h], 1 - ProgressConfig.Alpha / 2),
                residuals[h].Count))
            .ToList();

        var metrics = new List<ProgressHorizonMetric>();
        foreach (var h in horizons)
        {
            var band = bands.First(b => b.Horizon == h);
            foreach (var (label, _) in Predictors)
            {
                var e = errors[(label, h)];
                if (e.Count == 0) continue;
                // Coverage is only meaningful for the predictor the band was fitted to, and it is
                // in-sample here — reported so it is visible, never presented as a held-out guarantee.
                double? coverage = label == "pace" && residuals[h].Count > 0
                    ? (double)residuals[h].Count(r => r >= band.P10 && r <= band.P90) / residuals[h].Count
                    : null;
                metrics.Add(new ProgressHorizonMetric(label, h, e.Count, Math.Round(e.Average(), 4), coverage is double c ? Math.Round(c, 4) : null));
            }
        }

        return new ProgressValidationSummary
        {
            Provenance = "Rolling-origin back-test of physical percent-complete: predict Actual_Pct_Complete(o+h) "
                       + "from rows at or before o. Four predictors scored on identical eligible rows.",
            OriginMin = oMin == int.MaxValue ? 0 : oMin,
            OriginMax = oMax == int.MinValue ? 0 : oMax,
            Centres = byCentre.Count,
            Metrics = metrics.OrderBy(m => m.Horizon).ThenBy(m => m.Predictor, StringComparer.Ordinal).ToList(),
            Bands = bands,
            Notes = new[]
            {
                "MAE is in percentage points of progress, not currency — the target is already a percentage.",
                $"Bands are the empirical {(int)((1 - ProgressConfig.Alpha) * 100)}% residual quantiles of the 'pace' predictor at each horizon.",
                $"No horizon past {ProgressConfig.BacktestedHorizon} is scored: scoring one needs a reported period to score it against, and the panel does not reach that far.",
                "Coverage is in-sample (the bands are fitted on these same residuals) and is reported for visibility, not as a held-out guarantee.",
                $"Frozen: pace window={ProgressConfig.PaceWindow} periods, alpha={ProgressConfig.Alpha}, max horizon={ProgressConfig.MaxHorizon}.",
            },
        };
    }

    /// <summary>Reported percent complete at a period, or null when the row or the value is absent.</summary>
    private static double? At(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int period)
        => byPeriod.TryGetValue(period, out var r) && r.ActualPctComplete is double d && double.IsFinite(d) ? d : null;

    /// <summary>Progress at o, carried forward h periods at the given pace and capped at 100%.</summary>
    private static double? Project(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int o, int h, double? pace)
    {
        if (At(byPeriod, o) is not double a || pace is not double p || !double.IsFinite(p)) return null;
        // Clamped at zero for the same reason the live projection clamps: negative reported progress is
        // a re-measurement, and carrying it forward would un-build work that exists.
        return Math.Clamp(a + Math.Max(0, p) * h, 0, 100);
    }

    /// <summary>Linear-interpolated empirical quantile. Zero on an empty sample — a band of zero width,
    /// which correctly says "no spread was measured" rather than inventing one.</summary>
    internal static double Quantile(List<double> values, double q)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToList();
        if (sorted.Count == 1) return Math.Round(sorted[0], 4);
        double pos = Math.Clamp(q, 0, 1) * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        double frac = pos - lo;
        return Math.Round(sorted[lo] + (sorted[hi] - sorted[lo]) * frac, 4);
    }
}
