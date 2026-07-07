using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// Fits the serving forecaster: per horizon a ridge P50 (point) plus a CROSS-FITTED out-of-fold
/// residual store (never in-sample) for nominal-80% intervals; serves forecasts anchored ONLY at the
/// latest origin. See the plan's Core module for the leakage/interval contracts.
/// </summary>
public sealed class IncrementalSpendForecaster
{
    private sealed record Resid(string Bcc, int Feat, int Fold, int Bin, double R);
    private sealed class HorizonServing { public RidgeRegressor? Ridge; public List<Resid> Oof = new(); }

    private readonly ForecastFeatureBuilder _fb = new();
    private int _origin;
    private Dictionary<string, Dictionary<int, CostCentrePeriod>> _byCentre = new();
    private Dictionary<(string, int, int), IncrementSample> _byKey = new();   // (bcc, featurePeriod, h)
    private readonly Dictionary<int, HorizonServing> _serving = new();
    private readonly Random _rng = new(0);

    public int OriginPeriod => _origin;

    public void Fit(IReadOnlyList<CostCentrePeriod> panel, ReportingOrigins origins)
    {
        _origin = origins.ForecastPeriod;
        _byCentre = panel.GroupBy(r => r.BccId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.PeriodId), StringComparer.Ordinal);
        var samples = _fb.BuildAll(panel);
        _byKey = samples.ToDictionary(s => (s.BccId, s.FeaturePeriod, s.Horizon));

        foreach (var h in ForecastConfig.Horizons)
        {
            var elig = samples.Where(s => s.Horizon == h && s.Label.HasValue).ToList();
            var hs = new HorizonServing();
            // cross-fitted OOF residuals (each row scored by a model that did not train on it)
            for (int f = 0; f < ForecastConfig.KFolds; f++)
            {
                var train = elig.Where(s => s.Fold != f).ToList();
                if (train.Count < ForecastConfig.MinTrainRows) continue;
                var ridge = TryFit(train);
                if (ridge is null) continue;
                foreach (var s in elig.Where(s => s.Fold == f))
                    hs.Oof.Add(new Resid(s.BccId, s.FeaturePeriod, s.Fold,
                        ForecastConfig.ProgressBin(s.ProgressPct), s.Label!.Value - ridge.Predict(ForecastFeatureBuilder.Design(s))));
            }
            hs.Ridge = TryFit(elig);   // all-data fit for the point prediction only
            _serving[h] = hs;
        }
    }

    internal static RidgeRegressor? TryFit(IReadOnlyList<IncrementSample> rows)
    {
        if (rows.Count == 0) return null;
        var x = rows.Select(ForecastFeatureBuilder.Design).ToArray();
        var y = rows.Select(s => s.Label!.Value).ToArray();
        foreach (var lambda in new[] { ForecastConfig.Lambda }.Concat(ForecastConfig.RidgeFloorRetries))
        {
            try { var r = new RidgeRegressor(); r.Fit(x, y, lambda); return r; }
            catch (RidgeRegressor.NotFittableException) { }
        }
        return null;
    }

    // ── serving ──
    public CentreForecast? ForecastCentre(string bccId)
    {
        if (!_byCentre.TryGetValue(bccId, out var byPeriod) || !byPeriod.TryGetValue(_origin, out var cur)) return null;
        double progress = cur.ActualPctComplete is double p && double.IsFinite(p) ? p : 0.0;
        double bac = cur.BacAed is double b && double.IsFinite(b) ? b : 0.0;
        double ac = cur.AcCumulative is double a && double.IsFinite(a) ? a : 0.0;

        var bands = new List<HorizonBand>();
        var p50 = new double[ForecastConfig.Horizons.Length];
        bool h1Available = false;
        for (int i = 0; i < ForecastConfig.Horizons.Length; i++)
        {
            int h = ForecastConfig.Horizons[i];
            if (!_byKey.TryGetValue((bccId, _origin, h), out var s) || !_serving.TryGetValue(h, out var hs))
            { bands.Add(new HorizonBand(h, 0, null, null, false)); continue; }
            // model works in BAC-fraction space; convert to cost via × BAC
            double predFrac = hs.Ridge is not null ? hs.Ridge.Predict(ForecastFeatureBuilder.Design(s))
                                                   : (double.IsFinite(s.Features[5]) ? s.Features[5] : 0.0); // run-rate fallback (fraction)
            double predCost = predFrac * bac;
            p50[i] = predCost;
            var (lo, hi, avail) = Interval(hs.Oof, ForecastConfig.ProgressBin(progress));   // residual quantiles in fraction
            bands.Add(new HorizonBand(h, predCost,
                avail ? (predFrac + lo!.Value) * bac : null, avail ? (predFrac + hi!.Value) * bac : null, avail));
            if (h == 1) h1Available = avail;
        }

        var (cone, coneAvail) = CumulativeCone(progress, ac, bac, p50);
        var trust = progress < ForecastConfig.ProgressGatePct ? TrustBadge.TooEarly
                  : !h1Available ? TrustBadge.InsufficientCalibration : TrustBadge.Validatable;

        double? finalCost = IncrementHelper.RollCpi(byPeriod, _origin) is double rc && rc > 1e-6 && cur.EvAed is double ev
            ? ac + (bac - ev) / rc : null;

        return new CentreForecast
        {
            BccId = bccId, OriginPeriod = _origin, ProgressPct = progress, Bac = bac, AcAtOrigin = ac,
            Trust = trust, Increments = bands, CumulativeCone = cone, CumulativeConeAvailable = coneAvail,
            DirectionalFinalCost = finalCost,
        };
    }

    public IReadOnlyList<CentreForecast> AllCentres()
        => _byCentre.Keys.OrderBy(k => k, StringComparer.Ordinal)
            .Select(ForecastCentre).Where(f => f is not null).Select(f => f!).ToList();

    /// <summary>Short-horizon (h=1) project spend-scenario distribution via empirical residual draws
    /// across centres (a SCENARIO, not a probability; assumes centre independence → understates common shocks).</summary>
    public ProjectSpendScenario Rollup()
    {
        if (!_serving.TryGetValue(1, out var hs)) return new ProjectSpendScenario(_origin, 0, 0, 0, 0, 0);
        // per-centre h=1 cost P50 + a fraction-residual pool (bin-local → pooled); cost = fraction × BAC
        var centres = new List<(double p50Cost, double bac, List<double> poolFrac)>();
        var pooled = hs.Oof.Select(r => r.R).ToList();
        foreach (var bcc in _byCentre.Keys)
        {
            if (!_byKey.TryGetValue((bcc, _origin, 1), out var s) || _byCentre[bcc][_origin].ActualPctComplete is not double pr) continue;
            double predFrac = hs.Ridge is not null ? hs.Ridge.Predict(ForecastFeatureBuilder.Design(s))
                                                   : (double.IsFinite(s.Features[5]) ? s.Features[5] : 0.0);
            int bin = ForecastConfig.ProgressBin(pr);
            var pool = hs.Oof.Where(r => r.Bin == bin).Select(r => r.R).ToList();
            if (pool.Count < ForecastConfig.MinCount) pool = pooled;
            if (pool.Count > 0) centres.Add((predFrac * s.Bac, s.Bac, pool));
        }
        if (centres.Count == 0) return new ProjectSpendScenario(_origin, 0, 0, 0, 0, 0);

        var totals = new double[ForecastConfig.MonteCarloDraws];
        for (int d = 0; d < totals.Length; d++)
        {
            double t = 0;
            foreach (var (p50c, bac, pool) in centres) t += Math.Max(0, p50c + pool[_rng.Next(pool.Count)] * bac);
            totals[d] = t;
        }
        Array.Sort(totals);
        return new ProjectSpendScenario(_origin, Quantile(totals, 0.10), Quantile(totals, 0.50), Quantile(totals, 0.90),
            centres.Count, ForecastConfig.MonteCarloDraws);
    }

    // ── internals ──
    private (double? lo, double? hi, bool avail) Interval(List<Resid> oof, int bin)
    {
        var r = oof.Where(x => x.Bin == bin).Select(x => x.R).ToList();
        if (r.Count < ForecastConfig.MinCount) r = oof.Select(x => x.R).ToList();      // pooled fallback
        if (r.Count < ForecastConfig.MinCount) return (null, null, false);             // unavailable
        var (lo, hi) = ConformalResidQuantiles(r);
        return (lo ?? double.NegativeInfinity, hi ?? double.PositiveInfinity, true);
    }

    internal static (double? lo, double? hi) ConformalResidQuantiles(List<double> resids)
    {
        var s = resids.OrderBy(x => x).ToArray(); int n = s.Length;
        int lowerRank = (int)Math.Floor((n + 1) * (ForecastConfig.Alpha / 2));       // 1-indexed
        int upperRank = (int)Math.Ceiling((n + 1) * (1 - ForecastConfig.Alpha / 2));
        double? lo = lowerRank is >= 1 && lowerRank <= n ? s[lowerRank - 1] : null;   // null → −∞
        double? hi = upperRank is >= 1 && upperRank <= n ? s[upperRank - 1] : null;   // null → +∞
        return (lo, hi);
    }

    /// <summary>Cumulative cone via COMPLETE-CASE joint residual-path simulation (never endpoint summing).</summary>
    private (IReadOnlyList<ConePoint>, bool) CumulativeCone(double progress, double acAtOrigin, double bac, double[] p50)
    {
        // complete-case keys present in all three horizon stores under the same fold
        if (!_serving.TryGetValue(1, out var s1) || !_serving.TryGetValue(2, out var s2) || !_serving.TryGetValue(3, out var s3))
            return (Array.Empty<ConePoint>(), false);
        var d1 = s1.Oof.ToDictionary(r => (r.Bcc, r.Feat, r.Fold), r => r);
        var d2 = s2.Oof.ToDictionary(r => (r.Bcc, r.Feat, r.Fold), r => r);
        var d3 = s3.Oof.ToDictionary(r => (r.Bcc, r.Feat, r.Fold), r => r);
        int bin = ForecastConfig.ProgressBin(progress);
        var paths = d1.Keys.Where(k => d2.ContainsKey(k) && d3.ContainsKey(k))
            .Select(k => (r1: d1[k].R, r2: d2[k].R, r3: d3[k].R, bin: d1[k].Bin)).ToList();
        var binPaths = paths.Where(p => p.bin == bin).ToList();
        var use = binPaths.Count >= ForecastConfig.MinCount ? binPaths : paths;
        if (use.Count < ForecastConfig.MinCount) return (Array.Empty<ConePoint>(), false);

        int H = ForecastConfig.Horizons.Length;
        var sims = new double[H][]; for (int h = 0; h < H; h++) sims[h] = new double[use.Count];
        for (int i = 0; i < use.Count; i++)
        {
            var (r1, r2, r3, _) = use[i];
            double[] rr = { r1 * bac, r2 * bac, r3 * bac };   // residuals are in fraction → × BAC to cost
            double cum = acAtOrigin;
            for (int h = 0; h < H; h++) { cum += p50[h] + rr[h]; sims[h][i] = cum; }
        }
        var cone = new List<ConePoint>();
        for (int h = 0; h < H; h++)
        {
            Array.Sort(sims[h]);
            cone.Add(new ConePoint(_origin + ForecastConfig.Horizons[h],
                Quantile(sims[h], 0.50), Quantile(sims[h], 0.10), Quantile(sims[h], 0.90)));
        }
        return (cone, true);
    }

    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        double pos = q * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos); int hi = (int)Math.Ceiling(pos);
        return lo == hi ? sorted[lo] : sorted[lo] + (pos - lo) * (sorted[hi] - sorted[lo]);
    }
}
