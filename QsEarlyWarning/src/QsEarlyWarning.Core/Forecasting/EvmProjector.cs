using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// A cost-centre EVM position at any period, measured or projected — the two existing forecasters
/// composed at the reporting layer.
///
/// <b>This fits nothing.</b> It owns no model and estimates no parameter; it reads
/// <see cref="ProgressForecaster"/> for physical progress and <see cref="IncrementalSpendForecaster"/>
/// for spend and states what the two of them jointly imply. That is why it is constructed per request
/// rather than hung off the project snapshot beside the engines it composes.
///
/// <b>The one derivation it performs, and its licence.</b> EV is <c>BAC × pct/100</c> — the schema's own
/// generated-column definition (<c>0002_schema.sql</c>), evaluated on a projected percentage instead of a
/// reported one. It is not a cost model. AC is never derived this way: it comes from the spend
/// forecaster or the row reports it as unavailable. See <see cref="ProjectedModels"/> for the full
/// argument.
///
/// <b>Two assumptions, both stated on every response.</b>
/// <list type="number">
///   <item>Past the spend forecaster's back-tested horizon (h &gt; 3) the remaining work is priced at the
///         cost performance the cone ends on — the classic directional cost-to-complete, already used
///         elsewhere in this namespace for EAC — and the band widened by √(h/H), the same random-walk
///         widening <see cref="ProgressForecaster"/> applies past its own back-test. CPI is therefore
///         flat past that point, which is the honest reading: with no independent spend signal left,
///         "performance continues as observed" is all the data supports. The <i>moving</i> CPI lives in
///         the periods where both engines are back-tested, which is where the early warning is.</item>
///   <item>A centre that reaches 100% stops spending: past its projected finish, AC is frozen at the value
///         it had there. Without this, EV plateaus at BAC while AC climbs forever and CPI decays on a
///         centre that is finished.</item>
/// </list>
/// </summary>
public sealed class EvmProjector
{
    private readonly IReadOnlyList<CostCentrePeriod> _panel;
    private readonly Dictionary<string, IReadOnlyDictionary<int, CostCentrePeriod>> _byCentre;
    private readonly ProgressForecaster? _progress;
    private readonly IncrementalSpendForecaster? _spend;

    public EvmProjector(
        IReadOnlyList<CostCentrePeriod> panel,
        ProgressForecaster? progress,
        IncrementalSpendForecaster? spend,
        int origin)
    {
        _panel = panel;
        _progress = progress;
        _spend = spend;
        OriginPeriod = origin;
        MinPeriod = panel.Count > 0 ? panel.Min(p => p.PeriodId) : origin;
        _byCentre = panel
            .GroupBy(p => p.BccId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<int, CostCentrePeriod>)g
                    .GroupBy(p => p.PeriodId)
                    .ToDictionary(x => x.Key, x => x.First()),
                StringComparer.OrdinalIgnoreCase);
    }

    public int MinPeriod { get; }
    public int OriginPeriod { get; }

    /// <summary>Furthest period this projector will serve — the progress forecaster's hard cap.</summary>
    public int MaxPeriod => OriginPeriod + ProgressConfig.MaxHorizon;

    /// <summary>Last period the spend forecaster's back-test reaches.</summary>
    public int SpendBacktestedThroughPeriod => OriginPeriod + ForecastConfig.Horizons.Max();

    /// <summary>True when a projection can be served at all. Measured periods never need one.</summary>
    public bool CanProject => _progress is not null;

    /// <summary>
    /// The panel at <paramref name="period"/>, restricted to <paramref name="bccIds"/> when given.
    ///
    /// At or below the origin this is a passthrough of the reported rows — same figures, same order as
    /// <c>/api/v1/cost-centres</c> — so a caller has one shape and one code path across the boundary.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Period outside [MinPeriod, MaxPeriod].</exception>
    /// <exception cref="InvalidOperationException">Past the origin with no progress forecaster fitted.</exception>
    public ProjectedPanel ProjectAt(int period, IReadOnlyCollection<string>? bccIds = null)
    {
        if (period < MinPeriod || period > MaxPeriod)
            throw new ArgumentOutOfRangeException(
                nameof(period), period, $"period must be in [{MinPeriod}, {MaxPeriod}].");

        return period <= OriginPeriod ? Measured(period, bccIds) : Projected(period, bccIds);
    }

    // ── measured: the reported row, untouched ──
    private ProjectedPanel Measured(int period, IReadOnlyCollection<string>? bccIds)
    {
        var wanted = bccIds is { Count: > 0 }
            ? new HashSet<string>(bccIds, StringComparer.OrdinalIgnoreCase)
            : null;

        var rows = _panel
            .Where(r => r.PeriodId == period && (wanted is null || wanted.Contains(r.BccId)))
            .OrderBy(r => r.BccId, StringComparer.Ordinal)
            .Select(r => new ProjectedCentreRow
            {
                BccId = r.BccId,
                PeriodId = period,
                Basis = ProjectionBasis.Measured,
                Discipline = r.Discipline,
                PackageCode = r.PackageCode,
                Lifecycle = LifecycleOf(r.AlertLevel),
                Bac = r.BacAed ?? 0,
                PctComplete = r.ActualPctComplete ?? 0,
                Ev = r.EvAed ?? 0,
                Ac = r.AcCumulative,
                AcAvailable = r.AcCumulative is not null,
                Cv = r.CvAed,
                Cpi = r.Cpi,
                Eac = r.EacAed,
                Vac = r.VacAed,
                PctBudgetConsumed = r.PctBudgetConsumed,
                Pv = r.PvAed,
                Spi = r.Spi,
                PlannedPct = r.PlanPctComplete,
                AlertLevel = r.AlertLevel ?? "GREEN",
                AlertProjected = false,
            })
            .ToList();

        return new ProjectedPanel
        {
            Period = period,
            OriginPeriod = OriginPeriod,
            HorizonPeriod = period,
            BacktestedThroughPeriod = OriginPeriod + ProgressConfig.BacktestedHorizon,
            SpendBacktestedThroughPeriod = SpendBacktestedThroughPeriod,
            Basis = ProjectionBasis.Measured,
            Method = "reported",
            PvAvailable = true,
            PvReason = null,
            Notes = Array.Empty<string>(),
            Centres = rows,
        };
    }

    // ── projected: progress → EV, spend cone → AC, and what the two imply ──
    private ProjectedPanel Projected(int period, IReadOnlyCollection<string>? bccIds)
    {
        if (_progress is null)
            throw new InvalidOperationException("no progress projection fitted for this project.");

        var forecast = _progress.Project(bccIds, period);
        int h = period - OriginPeriod;
        bool spendBacktested = period <= SpendBacktestedThroughPeriod;

        var rows = new List<ProjectedCentreRow>();
        foreach (var centre in forecast.Centres)
        {
            var point = centre.Points.FirstOrDefault(p => p.Period == period);
            if (point is null) continue;                       // stalled centre past its own flat line
            if (!_byCentre.TryGetValue(centre.BccId, out var byPeriod)) continue;
            if (!byPeriod.TryGetValue(OriginPeriod, out var originRow)) continue;

            double bac = originRow.BacAed ?? 0;
            double ev = Money(bac * point.P50Pct / 100.0);
            double? evLo = point.P10Pct is double lo ? Money(bac * lo / 100.0) : null;
            double? evHi = point.P90Pct is double hi ? Money(bac * hi / 100.0) : null;

            var ac = AcAt(centre, bac, h);
            var progressTier = point.Tier == ProgressTier.Measured
                ? ProjectionBasis.Measured
                : point.Tier == ProgressTier.Forecast ? ProjectionBasis.Forecast : ProjectionBasis.Extrapolated;

            // The weaker of the two claims wins. A back-tested progress point married to an
            // extrapolated spend figure is an extrapolated row, and says so.
            var basis = !ac.Available
                ? progressTier
                : (ProjectionBasis)Math.Max((int)progressTier, (int)(spendBacktested ? ProjectionBasis.Forecast : ProjectionBasis.Extrapolated));

            double? cv = ac.P50 is double a1 ? Money(ev - a1) : null;
            double? cpi = ac.P50 is double a2 && a2 > 0 ? ev / a2 : null;

            // Same CASE the EVM view uses: with no earned value there is no ratio to scale BAC by,
            // so EAC falls back to BAC rather than dividing by zero.
            double? eac = ac.P50 is double a3 ? (ev > 0 ? Money(bac * a3 / ev) : bac) : null;
            double? vac = eac is double e ? Money(bac - e) : null;
            double? consumed = ac.P50 is double a4 && bac > 0 ? 100.0 * a4 / bac : null;

            string lifecycle = LifecycleOf(centre.AlertAtOrigin);
            bool verdictProjected = lifecycle == "IN_PROGRESS" && cpi is not null;
            string alert = verdictProjected
                ? (cpi! < EvmThresholds.CpiThreshold ? "AMBER" : "GREEN")
                : centre.AlertAtOrigin ?? "GREEN";

            rows.Add(new ProjectedCentreRow
            {
                BccId = centre.BccId,
                PeriodId = period,
                Basis = basis,
                Discipline = originRow.Discipline,
                PackageCode = originRow.PackageCode,
                Lifecycle = lifecycle,
                Bac = bac,
                PctComplete = point.P50Pct,
                PctP10 = point.P10Pct,
                PctP90 = point.P90Pct,
                Ev = ev,
                EvP10 = evLo,
                EvP90 = evHi,
                Ac = ac.P50,
                AcP10 = ac.P10,
                AcP90 = ac.P90,
                AcAvailable = ac.Available,
                AcNote = ac.Note,
                Cv = cv,
                Cpi = cpi,
                Eac = eac,
                Vac = vac,
                PctBudgetConsumed = consumed,
                Pv = null,
                Spi = null,
                PlannedPct = null,
                AlertLevel = alert,
                AlertProjected = verdictProjected,
                ProjectedFinishPeriod = centre.ProjectedFinishPeriod,
                PacePctPerPeriod = centre.PacePctPerPeriod,
                Stalled = centre.Stalled,
            });
        }

        var notes = new List<string>
        {
            "EV is BAC × projected percent complete — the schema's own definition of earned value, "
                + "evaluated on a projected percentage. It is not a second cost model.",
            "AC comes from the incremental-spend forecaster. Where that forecaster has nothing to say "
                + "about a centre, AC, CPI, EAC and VAC are null rather than guessed.",
        };
        if (!spendBacktested)
            notes.Add($"Past period {SpendBacktestedThroughPeriod} the spend projection prices the remaining "
                + "work at the cost performance observed there and widens the band by √(h/H). CPI is flat "
                + "beyond that point and no accuracy is measured this far out.");
        notes.Add("A centre that reaches 100% is assumed to stop spending: past its projected finish, AC is "
            + "held at the value it had there.");

        return new ProjectedPanel
        {
            Period = period,
            OriginPeriod = OriginPeriod,
            HorizonPeriod = forecast.HorizonPeriod,
            BacktestedThroughPeriod = forecast.BacktestedThroughPeriod,
            SpendBacktestedThroughPeriod = SpendBacktestedThroughPeriod,
            Basis = rows.Count > 0 ? (ProjectionBasis)rows.Max(r => (int)r.Basis) : ProjectionBasis.Forecast,
            Method = $"{forecast.Method} → EV; incremental-spend cone → AC",
            PvAvailable = false,
            PvReason = $"The baseline curve ends at period {OriginPeriod}, the same period the actuals do, "
                + "so there is no planned value past it to compare against — and therefore no SPI.",
            Notes = notes,
            Centres = rows,
        };
    }

    // ── the spend leg ──
    private readonly record struct AcPoint(double? P50, double? P10, double? P90, bool Available, string? Note);

    /// <summary>Cumulative AC at horizon <paramref name="h"/>, frozen at the centre's projected finish.</summary>
    private AcPoint AcAt(CentreProgressForecast centre, double bac, int h)
    {
        var sf = _spend?.ForecastCentre(centre.BccId);
        if (sf is null)
            return new AcPoint(null, null, null, false,
                _spend is null
                    ? "No spend forecaster fitted for this project."
                    : "This centre has no spend forecast at the current origin.");
        if (!sf.CumulativeConeAvailable || sf.CumulativeCone.Count == 0)
            return new AcPoint(null, null, null, false,
                "Not enough jointly-calibrated history to project spend for this centre.");

        // Past its projected finish the centre is done, so its spend stops there rather than running on.
        int cap = centre.ProjectedFinishPeriod is int f
            ? Math.Max(1, Math.Min(h, f - OriginPeriod))
            : h;

        var cone = sf.CumulativeCone;
        int n = cone.Count;

        double? loWidth = cone[n - 1].P10 is double l ? Math.Max(0, cone[n - 1].P50 - l) : null;
        double? hiWidth = cone[n - 1].P90 is double u ? Math.Max(0, u - cone[n - 1].P50) : null;

        // Earned value at a horizon, from the same projected percentage EV itself is built from.
        double EvAt(int i) => bac * (PointAt(centre, OriginPeriod + i)?.P50Pct ?? 0) / 100.0;

        // Walked rather than jumped to, so the monotone clamp below sees every step: cumulative spend
        // cannot fall, and a widening band must never appear to move backwards.
        double prev50 = sf.AcAtOrigin, prevLo = sf.AcAtOrigin, prevHi = sf.AcAtOrigin;
        double v50 = sf.AcAtOrigin;
        double? vLo = sf.AcAtOrigin, vHi = sf.AcAtOrigin;

        // Fixed once the walk leaves the cone: the cost performance the cone itself ends on.
        double coneEndAc = 0, coneEndEv = 0;

        for (int i = 1; i <= cap; i++)
        {
            double raw50;
            double? rawLo, rawHi;
            if (i <= n)
            {
                raw50 = cone[i - 1].P50;
                rawLo = cone[i - 1].P10;
                rawHi = cone[i - 1].P90;
            }
            else
            {
                // Past the cone the spend model has nothing left to say, so the only defensible
                // statement is that cost performance continues as observed: the remaining work is
                // priced at the CPI the cone ends on. This is the same directional cost-to-complete
                // the rest of the app already uses for EAC (IncrementalSpendForecaster's
                // DirectionalFinalCost, QsAnalyticsTools.DirectionalEac) — unvalidated, and tagged
                // Extrapolated wherever it surfaces.
                //
                // The alternative — holding the last projected increment as a run-rate — flatlines on
                // any centre whose h=3 increment the ridge predicts at or below zero, and then EV
                // climbs against frozen spend and CPI improves for free. That reads as a centre coming
                // in under budget when nothing of the sort has been forecast.
                double cpiRef = coneEndAc > 0 ? coneEndEv / coneEndAc : 0;
                raw50 = cpiRef > 0
                    ? coneEndAc + Math.Max(0, EvAt(i) - coneEndEv) / cpiRef
                    : coneEndAc;

                double scale = Math.Sqrt((double)i / n);
                rawLo = loWidth is double lw ? raw50 - lw * scale : null;
                rawHi = hiWidth is double hw ? raw50 + hw * scale : null;
            }

            v50 = Math.Max(raw50, prev50);
            vLo = rawLo is double rl ? Math.Min(Math.Max(rl, prevLo), v50) : null;
            vHi = rawHi is double rh ? Math.Max(Math.Max(rh, prevHi), v50) : null;

            prev50 = v50;
            if (vLo is double kl) prevLo = kl;
            if (vHi is double kh) prevHi = kh;
            if (i == n) { coneEndAc = v50; coneEndEv = EvAt(i); }
        }

        // The trust badge is surfaced rather than used to withhold the figure, matching how
        // /forecast/cost-centres serves it: a centre below the progress gate still has a spend cone,
        // it just has one whose interval was not calibrated on centres that early.
        var notes = new List<string>();
        if (cap < h) notes.Add($"Held at period {OriginPeriod + cap}, where this centre is projected to finish.");
        else if (h > n)
            notes.Add($"Past period {OriginPeriod + n} the remaining work is priced at the cost performance "
                + "observed there — directional, band widened, accuracy unmeasured.");
        if (sf.Trust == TrustBadge.TooEarly)
            notes.Add("Below the progress gate at the origin — the spend interval is not calibrated this early.");
        else if (sf.Trust == TrustBadge.InsufficientCalibration)
            notes.Add("Too few calibration residuals in this centre's progress band for a measured interval.");

        return new AcPoint(Money(v50), vLo is double a ? Money(a) : null, vHi is double b ? Money(b) : null, true,
            notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    private static ProgressPoint? PointAt(CentreProgressForecast centre, int period)
        => centre.Points.FirstOrDefault(p => p.Period == period);

    /// <summary>Mirrors the mapping <c>DashboardController.LifecycleOf</c> applies to reported rows.</summary>
    private static string LifecycleOf(string? alert) => (alert ?? "").ToUpperInvariant() switch
    {
        "NOT STARTED" => "NOT_STARTED",
        "CLOSED" => "CLOSED",
        _ => "IN_PROGRESS",
    };

    private static double Money(double v) => double.IsFinite(v) ? Math.Round(v, 2) : 0;
}
