using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// Grouped rolling-origin back-test (the credibility artifact). For each evaluation origin `o`, the
/// training pool is every sample whose TARGET period is strictly before `o` (leakage-safe on the
/// label, not the feature period); it is split by the frozen centre-group rule into proper-training
/// (fits the fold's ridge) and calibration (yields residuals + intervals). Predictions realized
/// exactly at `o` are scored — model and all four baselines on the IDENTICAL eligible rows. Reports
/// the comparison; does not require the model to win.
/// </summary>
public sealed class ForecastEvaluator
{
    private readonly ForecastFeatureBuilder _fb = new();

    public ForecastValidationSummary Evaluate(IReadOnlyList<CostCentrePeriod> panel, ReportingOrigins origins)
    {
        var samples = _fb.BuildAll(panel).Where(s => s.Label.HasValue).ToList();
        var predictors = new[] { "model" }.Concat(ForecastBaselines.All.Select(b => b.Label)).ToArray();

        // (predictor, horizon) → rows ; separate early-band accumulators
        var all = new Dictionary<(string, int), List<ForecastMetrics.Row>>();
        var early = new Dictionary<(string, int), List<ForecastMetrics.Row>>();
        var fallbackCount = new Dictionary<(string, int), int>();
        foreach (var pr in predictors) foreach (var h in ForecastConfig.Horizons)
        { all[(pr, h)] = new(); early[(pr, h)] = new(); fallbackCount[(pr, h)] = 0; }

        int foldsEvaluated = 0, foldsSkipped = 0;
        int oMin = int.MaxValue, oMax = int.MinValue;

        for (int o = origins.FirstOrigin; o <= origins.ForecastPeriod; o++)
        {
            foreach (var h in ForecastConfig.Horizons)
            {
                var pool = samples.Where(s => s.Horizon == h && s.TargetPeriod < o).ToList();
                var proper = pool.Where(s => !ForecastConfig.IsCalibration(s.BccId)).ToList();
                var calib = pool.Where(s => ForecastConfig.IsCalibration(s.BccId)).ToList();
                var evalRows = samples.Where(s => s.Horizon == h && s.TargetPeriod == o).ToList();
                if (evalRows.Count == 0) continue;

                if (proper.Count < ForecastConfig.MinTrainRows || calib.Count < ForecastConfig.MinCalRows)
                { foldsSkipped++; continue; }
                var ridge = IncrementalSpendForecaster.TryFit(proper);
                if (ridge is null) { foldsSkipped++; continue; }
                foldsEvaluated++;
                oMin = Math.Min(oMin, o); oMax = Math.Max(oMax, o);

                // per-bin calibration residuals for the model's interval
                var residByBin = calib.GroupBy(s => ForecastConfig.ProgressBin(s.ProgressPct))
                    .ToDictionary(g => g.Key, g => g.Select(s => s.Label!.Value - ridge.Predict(ForecastFeatureBuilder.Design(s))).ToList());
                var pooledResid = calib.Select(s => s.Label!.Value - ridge.Predict(ForecastFeatureBuilder.Design(s))).ToList();

                foreach (var s in evalRows)
                {
                    bool isEarly = s.ProgressPct < ForecastConfig.ClaimBandPct;
                    double actual = s.Label!.Value * s.Bac;   // label is a fraction of BAC → cost
                    // model (predict fraction → × BAC to cost; residuals are in fraction, band × BAC)
                    double mpFrac = ridge.Predict(ForecastFeatureBuilder.Design(s));
                    var band = residByBin.TryGetValue(ForecastConfig.ProgressBin(s.ProgressPct), out var rb) && rb.Count >= ForecastConfig.MinCount ? rb
                             : pooledResid.Count >= ForecastConfig.MinCount ? pooledResid : null;
                    double? lo = null, hi = null;
                    if (band is not null)
                    {
                        var (ql, qh) = IncrementalSpendForecaster.ConformalResidQuantiles(band);
                        lo = ql is double a ? (mpFrac + a) * s.Bac : null; hi = qh is double b ? (mpFrac + b) * s.Bac : null;
                    }
                    Add(all, early, ("model", h), new ForecastMetrics.Row(s.BccId, s.Bac, actual, mpFrac * s.Bac, lo, hi), isEarly);

                    foreach (var (label, fn) in ForecastBaselines.All)
                    {
                        var (predFrac, fb) = fn(s);
                        if (fb) fallbackCount[(label, h)]++;
                        Add(all, early, (label, h), new ForecastMetrics.Row(s.BccId, s.Bac, actual, predFrac * s.Bac, null, null), isEarly);
                    }
                }
            }
        }

        return new ForecastValidationSummary
        {
            Provenance = "Grouped rolling-origin back-test; incremental-spend target ΔAC(k+h); target-period leakage guard; centres recur across folds (temporal OOF, not new-centre generalization).",
            OriginMin = oMin == int.MaxValue ? 0 : oMin, OriginMax = oMax == int.MinValue ? 0 : oMax,
            FoldsEvaluated = foldsEvaluated, FoldsSkipped = foldsSkipped,
            Overall = Summarize(all, fallbackCount),
            EarlyBand = Summarize(early, fallbackCount),
            Notes = new[]
            {
                "MAE-%BAC = mean over centres of mean(|error|/BAC); WAPE = global Σ|error|/Σ|actual|.",
                "Coverage is measured (nominal 80% interval), reported with n and a Wilson band; not asserted.",
                "Model + 4 baselines scored on identical eligible rows per (horizon).",
                $"Frozen: λ={ForecastConfig.Lambda}, bins=[{string.Join(",", ForecastConfig.ProgressBinEdges)}], minCount={ForecastConfig.MinCount}, split=hash(BccId)mod10 (0-6 train/7-9 calib).",
            },
        };
    }

    private static void Add(Dictionary<(string, int), List<ForecastMetrics.Row>> all,
        Dictionary<(string, int), List<ForecastMetrics.Row>> early, (string, int) key, ForecastMetrics.Row row, bool isEarly)
    {
        all[key].Add(row); if (isEarly) early[key].Add(row);
    }

    private static IReadOnlyList<HorizonMetric> Summarize(
        Dictionary<(string, int), List<ForecastMetrics.Row>> acc, Dictionary<(string, int), int> fallback)
    {
        var outp = new List<HorizonMetric>();
        foreach (var ((pred, h), rows) in acc.OrderBy(k => (k.Key.Item2, k.Key.Item1)))
        {
            if (rows.Count == 0) continue;
            var (cov, n) = ForecastMetrics.Coverage(rows);
            (double lo, double hi)? wilson = cov is double c ? ForecastMetrics.Wilson(c, n) : null;
            outp.Add(new HorizonMetric(pred, h, rows.Count,
                ForecastMetrics.MaePctOfBac(rows), ForecastMetrics.Wape(rows),
                cov, wilson?.lo, wilson?.hi, fallback[(pred, h)]));
        }
        return outp;
    }
}
