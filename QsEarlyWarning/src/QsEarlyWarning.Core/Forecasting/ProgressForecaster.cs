using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// Projects each cost centre's physical percent complete past the last reported period, so the 4D build
/// sequence can keep rising after the workbook stops.
///
/// <b>The method, in one line:</b> continue the pace of the last three reported periods.
///
/// That is deliberately the simplest thing that works, and "works" is a measured claim, not a
/// preference. <see cref="ProgressBacktest"/> scores it against three alternatives on this project's own
/// history — assuming no further progress, continuing the plan's recent pace, and continuing the plan's
/// pace discounted by SPI — and reports the comparison. On Tower X the recent-pace method carries a mean
/// absolute error of roughly 1.8 / 3.2 / 3.9 percentage points at one, two and three periods out, against
/// 2.9 / 5.4 / 7.6 for assuming the work simply stops.
///
/// A richer predictor is available in principle and is worth naming so the ceiling is visible: scaling
/// the plan's <i>future</i> increments by SPI scores about 1.3 / 1.5 / 1.9 pp. It is not used, because the
/// plan curve in <c>9_HISTORICAL_DATA</c> ends at the same period the actuals do — so past the origin
/// there is no future plan left to scale. A schedule-integrated version of this feature could reach that
/// accuracy; this one cannot, and says so rather than implying otherwise.
///
/// <b>What this deliberately does not do.</b> It produces no cost figure. Progress and spend are
/// different forecasts with different error structures, and the spend forecast lives in
/// <see cref="IncrementalSpendForecaster"/> and stays there.
///
/// Downstream, <see cref="EvmProjector"/> is allowed to turn these percentages into EV — that is the
/// schema's own definition of earned value (<c>BAC × pct</c>) evaluated on a projected input, not a
/// cost model. It is not allowed to derive AC from them, and does not: without the spend forecaster
/// it reports AC, CPI and EAC as unavailable rather than manufacturing a final-cost number with none
/// of the validation such a number needs. See <see cref="ProgressModels"/> for the full statement.
/// </summary>
public sealed class ProgressForecaster
{
    private readonly Dictionary<string, IReadOnlyDictionary<int, CostCentrePeriod>> _byCentre;
    private readonly int _origin;
    private readonly ProgressValidationSummary _validation;
    private readonly Dictionary<int, ProgressResidualQuantiles> _bands;

    public ProgressForecaster(IReadOnlyList<CostCentrePeriod> panel, int origin, ProgressValidationSummary validation)
    {
        _origin = origin;
        _validation = validation;
        _bands = validation.Bands.ToDictionary(b => b.Horizon);
        _byCentre = panel
            .GroupBy(p => p.BccId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<int, CostCentrePeriod>)g
                .GroupBy(p => p.PeriodId)
                .ToDictionary(x => x.Key, x => x.First()), StringComparer.OrdinalIgnoreCase);
    }

    public string Method => $"recent {ProgressConfig.PaceWindow}-period progress pace";

    /// <summary>
    /// Projects the named centres — all of them when <paramref name="bccIds"/> is null or empty.
    ///
    /// <paramref name="through"/> is the last period to emit; when null it is the period the slowest
    /// requested centre tops out. Either way it is capped at <see cref="ProgressConfig.MaxHorizon"/>
    /// past the origin.
    /// </summary>
    public ProgressForecast Project(IReadOnlyCollection<string>? bccIds, int? through)
    {
        var wanted = bccIds is { Count: > 0 }
            ? bccIds.Where(_byCentre.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : _byCentre.Keys.ToList();

        int ceiling = _origin + ProgressConfig.MaxHorizon;

        // Pace and finish first, so the horizon can be derived from them before any point is emitted.
        var paces = wanted.ToDictionary(
            b => b,
            b => PaceFor(_byCentre[b]),
            StringComparer.OrdinalIgnoreCase);

        var finishes = wanted.ToDictionary(
            b => b,
            b => FinishFor(_byCentre[b], paces[b]),
            StringComparer.OrdinalIgnoreCase);

        // The slowest centre that actually finishes sets the suggested horizon. Stalled centres are
        // excluded on purpose: one centre with no pace must not stretch the timeline to the cap.
        var realFinishes = finishes.Values.Where(f => f.HasValue).Select(f => f!.Value).ToList();
        int suggested = realFinishes.Count > 0 ? Math.Min(realFinishes.Max(), ceiling) : _origin;

        int horizon = Math.Clamp(through ?? suggested, _origin, ceiling);

        var centres = wanted
            .OrderBy(b => b, StringComparer.Ordinal)
            .Select(b => Build(b, _byCentre[b], paces[b], finishes[b], horizon))
            .ToList();

        return new ProgressForecast
        {
            OriginPeriod = _origin,
            HorizonPeriod = horizon,
            BacktestedThroughPeriod = _origin + ProgressConfig.BacktestedHorizon,
            SuggestedHorizonPeriod = suggested,
            Method = Method,
            Centres = centres,
            Validation = _validation,
        };
    }

    private CentreProgressForecast Build(
        string bccId,
        IReadOnlyDictionary<int, CostCentrePeriod> byPeriod,
        double? pace,
        int? finish,
        int horizon)
    {
        double actual = At(byPeriod, _origin) ?? 0;
        bool stalled = pace is not double p || p <= 0;
        double effectivePace = stalled ? 0 : pace!.Value;

        var points = new List<ProgressPoint>();

        // The measured tail is included so the caller holds one uniform series and the
        // measured/projected boundary is a property of the data rather than an off-by-one in the UI.
        for (int period = Math.Max(byPeriod.Keys.Min(), _origin - ProgressConfig.PaceWindow + 1); period <= _origin; period++)
        {
            if (At(byPeriod, period) is not double measured) continue;
            points.Add(new ProgressPoint(period, Round(measured), Round(measured), Round(measured), ProgressTier.Measured));
        }

        // Projected periods. Monotone by construction (pace ≥ 0), and each band endpoint is additionally
        // held at or above the previous period's so a widening interval can never appear to move backwards.
        double prevLow = actual, prevHigh = actual;
        for (int period = _origin + 1; period <= horizon; period++)
        {
            int h = period - _origin;
            double p50 = Math.Clamp(actual + effectivePace * h, 0, 100);
            var (q10, q90) = BandFor(h);

            double low = Math.Clamp(Math.Max(p50 + q10, prevLow), 0, p50);
            double high = Math.Clamp(Math.Min(Math.Max(p50 + q90, prevHigh), 100), p50, 100);
            prevLow = low; prevHigh = high;

            points.Add(new ProgressPoint(period, Round(p50), Round(low), Round(high),
                h <= ProgressConfig.BacktestedHorizon ? ProgressTier.Forecast : ProgressTier.Extrapolated));
        }

        return new CentreProgressForecast
        {
            BccId = bccId,
            OriginPeriod = _origin,
            ActualPctAtOrigin = Round(actual),
            PacePctPerPeriod = Round(effectivePace),
            ProjectedFinishPeriod = finish,
            Stalled = stalled,
            AlertAtOrigin = byPeriod.TryGetValue(_origin, out var r) ? r.AlertLevel : null,
            Points = points,
        };
    }

    /// <summary>
    /// The residual quantiles for a horizon, as percentage-point offsets from the median projection.
    ///
    /// Past the back-tested horizon there is no measured spread, so the last measured one is widened by
    /// √(h/H) — the growth a random walk would show, and close to what the measured widths themselves do
    /// (6.4 → 10.9 → 14.4 pp at h=1,2,3 on Tower X). It is an assumption, recorded as one in the
    /// validation notes and marked on every point it touches by the <see cref="ProgressTier.Extrapolated"/> tier.
    /// </summary>
    private (double Q10, double Q90) BandFor(int h)
    {
        if (_bands.TryGetValue(h, out var exact)) return (exact.P10, exact.P90);
        if (!_bands.TryGetValue(ProgressConfig.BacktestedHorizon, out var last)) return (0, 0);
        double scale = Math.Sqrt((double)h / ProgressConfig.BacktestedHorizon);
        return (last.P10 * scale, last.P90 * scale);
    }

    /// <summary>Pace at the origin. Null when no adjacent reported increment exists to form one.</summary>
    private double? PaceFor(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod)
        => IncrementHelper.RecentProgressPace(byPeriod, _origin) is double p && double.IsFinite(p) ? p : null;

    /// <summary>
    /// First period the centre reads 100% at this pace, capped at the max horizon.
    ///
    /// Null when there is no forward pace — and that null is the honest answer, not a missing value. A
    /// centre sitting at 4% with nothing booked against it for three periods does not have a late finish
    /// date; it has no finish date that this data can support.
    /// </summary>
    private int? FinishFor(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, double? pace)
    {
        if (pace is not double p || p <= 0) return null;
        if (At(byPeriod, _origin) is not double actual) return null;
        if (actual >= 100) return _origin;

        int periods = (int)Math.Ceiling((100 - actual) / p);
        return periods <= ProgressConfig.MaxHorizon ? _origin + periods : null;
    }

    private static double? At(IReadOnlyDictionary<int, CostCentrePeriod> byPeriod, int period)
        => byPeriod.TryGetValue(period, out var r) && r.ActualPctComplete is double d && double.IsFinite(d) ? d : null;

    private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 4) : 0;
}
