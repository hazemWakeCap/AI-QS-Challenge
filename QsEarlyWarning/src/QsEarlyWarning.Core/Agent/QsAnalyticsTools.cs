using System.ComponentModel;
using QsEarlyWarning.Core.Forecasting;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Core.Variance;
using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Domain.ValueObjects;

namespace QsEarlyWarning.Core.Agent;

/// <summary>
/// Read-only analytics tools the copilot can call (idea-4). Built PER REQUEST from the caller's
/// tenant-scoped <see cref="ProjectSnapshot"/> (RLS membership is resolved in the controller before this
/// is constructed), so every tool reads the same project data the dashboard/forecast/stress-test serve —
/// the copilot can never diverge from them, and never touches a raw cell or a non-RLS static source.
///
/// Hard rules (tools compute, model narrates):
///  - Every AED figure is read from a pre-computed column or computed here; the model does NO arithmetic.
///  - Aggregated CPI = sum(EV)/sum(AC); aggregated SPI = sum(EV)/sum(PV) — never the mean of per-row ratios.
///  - Final-cost figures (EAC/VAC) are ONLY emitted by <see cref="DirectionalEac"/>, flagged validated=false.
///  - Every method validates/clamps its args and returns a typed { error } object rather than throwing.
///  - Every result carries a `sources` block (sheet, resolved period/filter, excluded count, row IDs).
/// </summary>
public sealed class QsAnalyticsTools
{
    private readonly ProjectSnapshot _snapshot;
    private readonly WatchlistScoringService _scoring;

    public QsAnalyticsTools(ProjectSnapshot snapshot, WatchlistScoringService scoring)
    {
        _snapshot = snapshot;
        _scoring = scoring;
    }

    private IReadOnlyList<CostCentrePeriod> Panel => _snapshot.Panel;

    // Scoring tools need a training origin (4..12); raw EVM/aggregation tools accept any present period.
    private static bool ScoreablePeriod(int p) => p is >= EvmThresholds.MinTrainOrigin and <= EvmThresholds.ForecastPeriod;
    private bool PresentPeriod(int p) => p >= _snapshot.MinPeriod && p <= _snapshot.ForecastPeriod;
    private static string Key(string bcc, int period) => $"{bcc}@P{period}";

    // ── idea-1 surface: watchlist / drift / per-centre EVM ──

    [Description("Get the ranked watchlist of GREEN cost centres most at risk of tipping to AMBER next " +
                 "period. periodId must be 4..12 (12 is the live forecast). topK is 5 or 10. Returns each " +
                 "centre's risk score, CPI, budget/progress gap (percentage points), and plain-language " +
                 "risk indicators.")]
    public object GetWatchlist(
        [Description("Reporting period, 4..12 (12 = live forecast)")] int periodId,
        [Description("How many top centres to return: 5 or 10")] int topK = 10)
    {
        if (!ScoreablePeriod(periodId)) return Err($"periodId must be {EvmThresholds.MinTrainOrigin}..{EvmThresholds.ForecastPeriod}.");
        if (topK is not (5 or 10)) topK = 10;

        var r = _scoring.ScorePeriod(Panel, periodId, _snapshot.Model);
        if (r.Status == ScoreStatus.NoArtifact) return Err($"No model serves period {periodId}.");

        var rows = r.Rows.Take(topK).ToList();
        return new
        {
            period = periodId,
            isForecast = r.IsForecast,
            eligibleCount = r.Rows.Count,
            rows = rows.Select((row, i) => new
            {
                rank = i + 1,
                bccId = row.BccId,
                discipline = row.Discipline,
                riskScore = Math.Round(row.RiskScore, 3),
                cpi = Math.Round(row.Cpi, 3),
                gapPp = Math.Round(row.Gap, 2),
                riskIndicators = row.RiskIndicators,
            }),
            sources = Src("9_HISTORICAL_DATA", periodId, $"top {topK} of {r.Rows.Count} scoreable GREEN centres",
                excluded: null, rowIds: rows.Select(row => Key(row.BccId, periodId))),
        };
    }

    [Description("Get one cost centre's recent EVM + trend history across periods (CPI, gap, alert level). " +
                 "Use to explain why a centre is or isn't on the watchlist.")]
    public object GetCostCentreDetail(
        [Description("Cost centre id, e.g. BCC-ARC-PAINT-317")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!PresentPeriod(periodId)) return Err($"periodId must be {_snapshot.MinPeriod}..{_snapshot.ForecastPeriod}.");

        var rows = Panel
            .Where(p => string.Equals(p.BccId, bccId, StringComparison.OrdinalIgnoreCase) && p.PeriodId <= periodId)
            .OrderBy(p => p.PeriodId)
            .TakeLast(4)
            .ToList();
        if (rows.Count == 0) return Err($"No rows for {bccId} at/under period {periodId}.");

        return new
        {
            bccId,
            history = rows.Select(p => new { period = p.PeriodId, alert = p.AlertLevel, cpi = p.Cpi, gapPp = p.Gap, spi = p.Spi }),
            sources = Src("9_HISTORICAL_DATA", periodId, $"{bccId} last {rows.Count} periods",
                excluded: null, rowIds: rows.Select(p => Key(p.BccId, p.PeriodId))),
        };
    }

    [Description("Explain a cost centre's cost drift at a period — for ANY centre and status, not only " +
                 "watchlist GREEN candidates. When the centre is a GREEN centre eligible for the watchlist, " +
                 "returns mode='watchlist': its tipping-risk score and the deterministic risk indicators " +
                 "(which threshold/trend conditions rank it high). When it has ALREADY drifted (AMBER) or is " +
                 "otherwise off the watchlist, returns mode='trajectory': its current CPI vs the 0.95 AMBER " +
                 "line, the period it first crossed, the CPI/gap/SPI trend, how many periods it has held its " +
                 "status, and a short history — so a QS can see HOW it drifted, not just that it isn't a " +
                 "candidate. Use for 'explain the drift for <BCC>' on any centre.")]
    public object ExplainDrift(
        [Description("Cost centre id")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!PresentPeriod(periodId)) return Err($"periodId must be {_snapshot.MinPeriod}..{_snapshot.ForecastPeriod}.");

        // Watchlist mode: only for a scoreable period where the centre is a GREEN tipping candidate.
        if (ScoreablePeriod(periodId))
        {
            var r = _scoring.ScorePeriod(Panel, periodId, _snapshot.Model);
            var row = r.Rows.FirstOrDefault(x => string.Equals(x.BccId, bccId, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
                return new
                {
                    mode = "watchlist",
                    bccId, periodId,
                    status = "GREEN",
                    onWatchlist = true,
                    riskScore = Math.Round(row.RiskScore, 3),
                    cpi = Round(row.Cpi),
                    gapPp = Round(row.Gap),
                    riskIndicators = row.RiskIndicators,
                    sources = Src("9_HISTORICAL_DATA", periodId, bccId, excluded: null, rowIds: new[] { Key(bccId, periodId) }),
                };
        }

        // Trajectory mode: any centre/status present at the period (already-AMBER, CLOSED, or GREEN-not-scoreable).
        return ExplainTrajectory(bccId, periodId);
    }

    /// <summary>
    /// Deterministic drift narrative for a centre that is NOT a GREEN watchlist tipping candidate at
    /// <paramref name="periodId"/> (already AMBER, CLOSED, or GREEN-but-unscoreable). Every figure is read
    /// from a pre-computed 9_HISTORICAL_DATA column or a simple period-over-period difference computed here
    /// (never by the model): current CPI vs the 0.95 AMBER line, the period it first crossed, the CPI trend,
    /// the budget-vs-progress gap, the schedule lane (SPI), and how long it has held its status.
    /// </summary>
    private object ExplainTrajectory(string bccId, int periodId)
    {
        var series = Panel
            .Where(p => string.Equals(p.BccId, bccId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.PeriodId)
            .ToList();
        if (series.Count == 0) return Err($"No rows for {bccId}.");

        var byPeriod = series.ToDictionary(p => p.PeriodId);
        if (!byPeriod.TryGetValue(periodId, out var cur)) return Err($"No row for {bccId} at period {periodId}.");

        const double line = EvmThresholds.CpiThreshold; // 0.95 AMBER line
        double? cpi = Fin(cur.Cpi), gap = Fin(cur.Gap), spi = Fin(cur.Spi);
        string? status = cur.AlertLevel;

        // CPI change vs the EXACT previous period only (null across a gap) — a difference, not model arithmetic.
        double? dCpi1 = byPeriod.TryGetValue(periodId - 1, out var prev)
            && Fin(prev.Cpi) is double b && cpi is double a ? a - b : (double?)null;

        // Earliest period (≤ periodId) whose recorded CPI first fell below the 0.95 line.
        int? firstBreach = series
            .Where(p => p.PeriodId <= periodId && Fin(p.Cpi) is double c && c < line)
            .Select(p => (int?)p.PeriodId)
            .FirstOrDefault();

        // Consecutive periods ending at periodId that hold the current status.
        int periodsAtStatus = 0;
        for (int q = periodId; byPeriod.TryGetValue(q, out var rr)
            && string.Equals(rr.AlertLevel, status, StringComparison.OrdinalIgnoreCase); q--) periodsAtStatus++;

        // Deterministic drift indicators (contextual, not causal), largest-magnitude first — same style as the watchlist.
        var items = new List<(double weight, string text)>();
        if (cpi is double c1)
            items.Add(c1 < line
                ? (line - c1 + 1.0, $"CPI {c1:0.000} — already {(line - c1):0.000} below the 0.95 AMBER line")
                : (0.2, $"CPI {c1:0.000} — at/above the 0.95 line"));
        if (dCpi1 is double d1 && d1 < 0) items.Add((-d1, $"CPI down {(-d1):0.000} since period {periodId - 1}"));
        else if (dCpi1 is double d1u && d1u > 0) items.Add((d1u * 0.5, $"CPI up {d1u:0.000} since period {periodId - 1} (recovering)"));
        if (gap is double g && g > 0) items.Add((g / 100.0, $"spending {g:0.0}pp ahead of progress"));
        if (spi is double s && s < 1.0) items.Add((1.0 - s, $"behind schedule — SPI {s:0.000}"));
        if (periodsAtStatus >= 2 && !string.Equals(status, "GREEN", StringComparison.OrdinalIgnoreCase))
            items.Add((0.4, $"held {status} for {periodsAtStatus} periods running"));
        if (items.Count == 0) items.Add((0, "insufficient signal to characterise drift"));

        var history = series.Where(p => p.PeriodId <= periodId).TakeLast(6).ToList();

        return new
        {
            mode = "trajectory",
            bccId, periodId,
            status,
            onWatchlist = false,
            watchlistNote = status is not null && !string.Equals(status, "GREEN", StringComparison.OrdinalIgnoreCase)
                ? $"Off the tipping watchlist — the watchlist ranks GREEN centres about to cross 0.95; this centre is {status}, already past the line, so its drift is described from its trajectory below."
                : "Not scored on the watchlist here (the watchlist ranks GREEN centres with finite CPI/progress inputs); drift is described from its trajectory below.",
            cpi = Round(cpi), gapPp = Round(gap), spi = Round(spi),
            amberLine = line,
            firstBreachedAmberAtPeriod = firstBreach,
            periodsAtStatus,
            driftIndicators = items.OrderByDescending(x => x.weight).Take(4).Select(x => x.text).ToList(),
            history = history.Select(p => new
            {
                period = p.PeriodId, alert = p.AlertLevel, cpi = Round(Fin(p.Cpi)), gapPp = Round(Fin(p.Gap)), spi = Round(Fin(p.Spi)),
            }),
            sources = Src("9_HISTORICAL_DATA", periodId, $"{bccId} trajectory (last {history.Count} periods)",
                excluded: null, rowIds: history.Select(p => Key(p.BccId, p.PeriodId))),
        };
    }

    [Description("Get the validated per-period EVM identities for a cost centre: CV, CPI, SPI. Does NOT " +
                 "return any final-cost figure (EAC/VAC) — use directional_eac for that, which is flagged " +
                 "unvalidated.")]
    public object GetEvmSnapshot(
        [Description("Cost centre id")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!PresentPeriod(periodId)) return Err($"periodId must be {_snapshot.MinPeriod}..{_snapshot.ForecastPeriod}.");

        var row = Panel.FirstOrDefault(p =>
            string.Equals(p.BccId, bccId, StringComparison.OrdinalIgnoreCase) && p.PeriodId == periodId);
        if (row is null) return Err($"No row for {bccId} at period {periodId}.");

        var e = EvmSnapshot.From(row);
        // CV/CPI/SPI only — EAC and VAC are final-cost-derived and live in directional_eac (G3).
        return new
        {
            bccId, periodId,
            cv = Round(e.Cv), cpi = Round(e.Cpi), spi = Round(e.Spi),
            sources = Src("9_HISTORICAL_DATA", periodId, bccId, excluded: null, rowIds: new[] { Key(bccId, periodId) }),
        };
    }

    // ── idea-2 surface: validated forecast vs directional EAC (G3) ──

    [Description("Get the VALIDATED next-period incremental-spend forecast for a cost centre: horizons " +
                 "h=1,2,3 with P10/P50/P90 spend increments, the forecast origin period, and a trust badge. " +
                 "This is the forecast a QS should trust. It deliberately does NOT return a final-cost number.")]
    public object ForecastIncrementalSpend(
        [Description("Cost centre id")] string bccId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (_snapshot.Forecaster is null) return Err("Forecast unavailable for this project (insufficient data to fit).");

        var f = _snapshot.Forecaster.ForecastCentre(bccId);
        if (f is null) return Err($"No forecast for '{bccId}' at the latest origin.");

        // Allowlisted projection — DirectionalFinalCost is deliberately NOT mapped (G3).
        return new
        {
            bccId = f.BccId,
            originPeriod = f.OriginPeriod,
            trust = f.Trust.ToString(),
            validated = true,
            increments = f.Increments.Select(b => new
            {
                horizon = b.Horizon, p10 = Round(b.P10), p50 = Round(b.P50), p90 = Round(b.P90), available = b.Available,
            }),
            sources = Src("9_HISTORICAL_DATA (forecast model)", f.OriginPeriod, $"{bccId} incremental spend h1-h3",
                excluded: null, rowIds: new[] { Key(f.BccId, f.OriginPeriod) }),
        };
    }

    [Description("Get the DIRECTIONAL final-cost extrapolation EAC = BAC/CPI (and VAC = BAC-EAC) for a cost " +
                 "centre. This is the workbook formula, NOT a validated forecast — it is returned flagged " +
                 "validated=false. For a forecast to trust, use forecast_incremental_spend instead.")]
    public object DirectionalEac(
        [Description("Cost centre id")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!PresentPeriod(periodId)) return Err($"periodId must be {_snapshot.MinPeriod}..{_snapshot.ForecastPeriod}.");

        var row = Panel.FirstOrDefault(p =>
            string.Equals(p.BccId, bccId, StringComparison.OrdinalIgnoreCase) && p.PeriodId == periodId);
        if (row is null) return Err($"No row for {bccId} at period {periodId}.");

        // Precondition: BAC/CPI must be present, finite, and CPI > 0 (no division by zero / NaN).
        if (row.BacAed is not double bac || !double.IsFinite(bac)
            || row.Cpi is not double cpi || !double.IsFinite(cpi) || cpi <= 0)
            return new { bccId, periodId, available = false, reason = "EAC = BAC/CPI is undefined here (missing BAC or CPI ≤ 0)." };

        var eac = bac / cpi;
        var vac = bac - eac;
        return new
        {
            bccId, periodId, validated = false,
            note = "Directional BAC/CPI extrapolation, not a validated forecast. Use forecast_incremental_spend to trust a forecast.",
            eac = Round(eac), vac = Round(vac), bac = Round(bac), cpi = Round(cpi),
            sources = Src("9_HISTORICAL_DATA", periodId, bccId, excluded: null, rowIds: new[] { Key(bccId, periodId) }),
        };
    }

    [Description("Run a UNIT-RATE WHAT-IF scenario for a cost centre: 'if we renegotiate / from next " +
                 "period the rate becomes X per unit, what happens?'. Reprices the centre's REMAINING " +
                 "quantity at the user-supplied AED/unit rate, keeping its recent physical pace, and returns " +
                 "the next 3 periods' spend, the scenario cost-to-complete, final cost and VAC. This is a " +
                 "user ASSUMPTION, not a validated forecast (returned validated=false, assumptionBased=true): " +
                 "the newUnitRate is the QS's input, not a sheet figure. Also returns the centre's current " +
                 "realized rate and planned rate so the scenario can be contrasted. For the trustworthy " +
                 "data-driven forecast instead, use forecast_incremental_spend.")]
    public object ScenarioForecast(
        [Description("Cost centre id")] string bccId,
        [Description("Assumed go-forward cost per unit (AED/unit), e.g. 299. Must be > 0.")] double newUnitRate,
        [Description("Optional period the new rate takes effect (default: next period after the latest).")] int? effectiveFromPeriod = null)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!double.IsFinite(newUnitRate) || newUnitRate <= 0) return Err("newUnitRate must be a positive number (AED per unit).");

        int origin = _snapshot.ForecastPeriod;
        var s = ScenarioForecaster.Rate(Panel, bccId, origin, newUnitRate, effectiveFromPeriod);
        if (!s.Available)
            return new { bccId, originPeriod = origin, available = false, reason = s.UnavailableReason };

        return new
        {
            bccId = s.BccId,
            originPeriod = s.OriginPeriod,
            validated = false,
            assumptionBased = true,
            note = "Scenario at a user-supplied unit rate — NOT a validated forecast. Assumes the remaining " +
                   "work continues at the recent physical pace, repriced at the given rate.",
            assumption = new
            {
                newUnitRate = Round(s.NewUnitRate),
                unit = s.Unit,
                effectiveFromPeriod = s.EffectiveFromPeriod,
                paceBasis = "recent earned-quantity run-rate (last ≤3 periods)",
            },
            baseline = new
            {
                plannedUnitRate = Round(s.PlannedUnitRate),      // = BAC / Budget_Qty
                currentRealizedRate = Round(s.CurrentRealizedRate), // = AC / earned qty
                remainingQty = Round(s.RemainingQty),
                budgetQty = Round(s.BudgetQty),
                recentQtyPacePerPeriod = Round(s.RecentQtyPacePerPeriod),
            },
            scenarioIncrements = s.Increments.Select(b => new
            {
                period = b.Period, qty = Round(b.Qty), spend = Round(b.Spend), unitRate = Round(b.UnitRate),
            }),
            scenarioCostToComplete = Round(s.ScenarioCostToComplete),
            scenarioFinalCost = Round(s.ScenarioFinalCost),
            scenarioVac = Round(s.ScenarioVac),
            sources = Src("9_HISTORICAL_DATA", origin, $"{bccId} scenario @ {Round(s.NewUnitRate)}/unit",
                excluded: null, rowIds: new[] { Key(bccId, origin) }),
        };
    }

    // ── resource attribution + project aggregation ──

    [Description("Get the resource-type split of actual cost for a cost centre at a period: manpower, " +
                 "material, equipment, subcontract amounts and their shares of total AC.")]
    public object ResourceSplit(
        [Description("Cost centre id")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!PresentPeriod(periodId)) return Err($"periodId must be {_snapshot.MinPeriod}..{_snapshot.ForecastPeriod}.");

        var row = Panel.FirstOrDefault(p =>
            string.Equals(p.BccId, bccId, StringComparison.OrdinalIgnoreCase) && p.PeriodId == periodId);
        if (row is null) return Err($"No row for {bccId} at period {periodId}.");

        double man = row.AcManpower ?? 0, mat = row.AcMaterial ?? 0, eq = row.AcEquipment ?? 0, sub = row.AcSubcontract ?? 0;
        double total = man + mat + eq + sub;
        double? Share(double v) => total > 0 ? Math.Round(v / total * 100, 1) : (double?)null;
        return new
        {
            bccId, periodId,
            manpower = Round(man), material = Round(mat), equipment = Round(eq), subcontract = Round(sub),
            totalAc = Round(total),
            sharesPct = new { manpower = Share(man), material = Share(mat), equipment = Share(eq), subcontract = Share(sub) },
            sources = Src("9_HISTORICAL_DATA", periodId, bccId, excluded: null, rowIds: new[] { Key(bccId, periodId) }),
        };
    }

    [Description("Get the aggregated project-level (or filtered) EVM ratios at a period: CPI = sum(EV)/sum(AC) " +
                 "and SPI = sum(EV)/sum(PV) — the correct aggregated form, NOT the mean of per-row ratios. " +
                 "Optionally filter by discipline or package code. Reports the rows included/excluded per ratio.")]
    public object ProjectEvm(
        [Description("Reporting period, 4..12")] int periodId,
        [Description("Optional discipline filter (matched loosely, e.g. 'civil')")] string? discipline = null,
        [Description("Optional estimate package code filter, e.g. EP-STR-CON")] string? packageCode = null)
    {
        if (!PresentPeriod(periodId)) return Err($"periodId must be {_snapshot.MinPeriod}..{_snapshot.ForecastPeriod}.");

        bool DiscOk(string? d) => discipline is null
            || (d is not null && d.Contains(discipline, StringComparison.OrdinalIgnoreCase));
        bool PkgOk(string? p) => packageCode is null || string.Equals(p, packageCode, StringComparison.OrdinalIgnoreCase);

        var scope = Panel.Where(p => p.PeriodId == periodId && DiscOk(p.Discipline) && PkgOk(p.PackageCode)).ToList();
        var filter = string.Join(" · ", new[]
        {
            $"period {periodId}",
            discipline is null ? null : $"discipline~'{discipline}'",
            packageCode is null ? null : $"package={packageCode}",
        }.Where(s => s is not null));

        // Ratio-specific eligibility keyed on the denominator only; numerator may be zero, but must be finite.
        object Block(Func<CostCentrePeriod, double?> denom)
        {
            var eligible = scope.Where(p =>
                p.EvAed is double ev && double.IsFinite(ev)
                && denom(p) is double d && double.IsFinite(d) && d > 0).ToList();
            double sumEv = eligible.Sum(p => p.EvAed!.Value);
            double sumDen = eligible.Sum(p => denom(p)!.Value);
            bool available = eligible.Count > 0 && sumDen > 0;
            return new
            {
                available,
                value = available ? (double?)Round(sumEv / sumDen) : null,
                sumEv = Round(sumEv), sumDenominator = Round(sumDen),
                includedCount = eligible.Count, excludedCount = scope.Count - eligible.Count,
                rowIds = eligible.Select(p => Key(p.BccId, p.PeriodId)).ToArray(),
            };
        }

        return new
        {
            period = periodId,
            filter,
            cpi = Block(p => p.AcCumulative),   // sum(EV)/sum(AC)
            spi = Block(p => p.PvAed),          // sum(EV)/sum(PV)
            note = "CPI = sum(EV)/sum(AC); SPI = sum(EV)/sum(PV). Aggregated, never the mean of per-row ratios.",
            sources = Src("9_HISTORICAL_DATA", periodId, filter, excluded: null,
                rowIds: scope.Select(p => Key(p.BccId, p.PeriodId))),   // per-ratio counts live in cpi/spi blocks
        };
    }

    // ── progress surface: plan vs actual percent-complete per centre ──

    [Description("List cost centres by PROGRESS at a period. Returns each centre's planned percent complete " +
                 "(Plan_Pct_Complete), actual percent complete (Actual_Pct_Complete), and the plan−actual gap. " +
                 "Use for questions like 'which BCCs have Plan_Pct_Complete < 100', 'centres not yet fully " +
                 "planned-complete', or 'which centres are behind plan'. The optional bounds filter on the raw " +
                 "percent columns (0-100): max bounds are STRICTLY LESS THAN, min bounds are >= (inclusive) — so " +
                 "Plan_Pct_Complete < 100 is maxPlanPct=100. Rows missing either field are excluded and counted.")]
    public object ListCentresByProgress(
        [Description("Reporting period")] int periodId,
        [Description("Optional: keep only centres with Plan_Pct_Complete strictly less than this (0-100). e.g. 100 = not yet fully planned-complete.")] double? maxPlanPct = null,
        [Description("Optional: keep only centres with Plan_Pct_Complete >= this (0-100).")] double? minPlanPct = null,
        [Description("Optional: keep only centres with Actual_Pct_Complete strictly less than this (0-100).")] double? maxActualPct = null,
        [Description("Optional: keep only centres with Actual_Pct_Complete >= this (0-100).")] double? minActualPct = null,
        [Description("Max rows to return (default 50, capped at 200).")] int limit = 50)
    {
        if (!PresentPeriod(periodId)) return Err($"periodId must be {_snapshot.MinPeriod}..{_snapshot.ForecastPeriod}.");
        int cap = Math.Clamp(limit, 1, 200);

        // Scoreable = BOTH progress fields finite; missing rows are excluded and COUNTED, never silently dropped.
        var inScope = Panel.Where(p => p.PeriodId == periodId).ToList();
        var scoreable = inScope.Where(p =>
            p.PlanPctComplete is double pl && double.IsFinite(pl)
            && p.ActualPctComplete is double ac && double.IsFinite(ac)).ToList();

        bool Keep(CostCentrePeriod p)
        {
            double plan = p.PlanPctComplete!.Value, act = p.ActualPctComplete!.Value;
            return (maxPlanPct is null || plan < maxPlanPct.Value)
                && (minPlanPct is null || plan >= minPlanPct.Value)
                && (maxActualPct is null || act < maxActualPct.Value)
                && (minActualPct is null || act >= minActualPct.Value);
        }

        var matched = scoreable.Where(Keep)
            .OrderBy(p => p.PlanPctComplete!.Value).ThenBy(p => p.BccId, StringComparer.Ordinal)
            .ToList();

        var filter = string.Join(" · ", new[]
        {
            $"period {periodId}",
            maxPlanPct is null ? null : $"planPct<{maxPlanPct}",
            minPlanPct is null ? null : $"planPct>={minPlanPct}",
            maxActualPct is null ? null : $"actualPct<{maxActualPct}",
            minActualPct is null ? null : $"actualPct>={minActualPct}",
        }.Where(s => s is not null));

        int excluded = inScope.Count - scoreable.Count;
        var rows = matched.Take(cap).Select(p => new
        {
            bccId = p.BccId,
            discipline = p.Discipline,
            packageCode = p.PackageCode,
            planPctComplete = Round(p.PlanPctComplete),
            actualPctComplete = Round(p.ActualPctComplete),
            planMinusActualPp = Round(p.PlanPctComplete!.Value - p.ActualPctComplete!.Value),
        }).ToList();

        return new
        {
            period = periodId,
            filter,
            matchedCount = matched.Count,
            returnedCount = rows.Count,
            excludedCount = excluded,
            truncated = matched.Count > rows.Count,
            rows,
            note = "Percent columns are 0-100. planMinusActualPp = plan − actual (positive ⇒ actual is behind plan).",
            sources = Src("9_HISTORICAL_DATA", periodId, filter, excluded,
                rowIds: matched.Select(p => Key(p.BccId, p.PeriodId))),
        };
    }

    // ── idea-3 surface: estimate assumption stress test (computed report only, G9) ──

    [Description("Get the estimate assumption stress-test findings for a package: the Class-1 reconciliation " +
                 "tie-out status and the Class-2 unusual-assumption flags (aggressive output norm, thin unit " +
                 "rate, thin/zero contingency). Estimate-side review prompts, not verdicts.")]
    public object StressFlagsForPackage(
        [Description("Estimate package code, e.g. EP-STR-CON")] string packageCode)
    {
        if (string.IsNullOrWhiteSpace(packageCode)) return Err("packageCode is required.");
        var st = _snapshot.StressTest;
        if (st is null) return new { packageCode, available = false, reason = "No estimate workbook for this project." };

        var flags = st.AssumptionFlags
            .Where(f => string.Equals(f.Package, packageCode, StringComparison.OrdinalIgnoreCase)).ToList();
        var recon = st.Reconciliation.Items
            .Where(i => string.Equals(i.Package, packageCode, StringComparison.OrdinalIgnoreCase)).ToList();
        if (flags.Count == 0 && recon.Count == 0)
            return new { packageCode, available = true, tieOut = (object?)null, flags = Array.Empty<object>(),
                reason = $"No stress-test rows for package '{packageCode}'." };

        var rowIds = flags.SelectMany(f => f.SourceItemRefs)
            .Concat(recon.Select(i => i.Scope))
            .Distinct(StringComparer.Ordinal).ToArray();

        return new
        {
            packageCode,
            available = true,
            rulesVersion = flags.FirstOrDefault()?.RulesVersion ?? EstimateStressVersion,
            tieOut = new { itemsChecked = recon.Count, itemsFailed = recon.Count(i => !i.TiesOut) },
            flags = flags.Select(f => new
            {
                kind = f.Kind, severity = f.Severity, reason = f.Reason, itemRefs = f.SourceItemRefs,
            }),
            sources = Src("1_BOQ / 2_ESTIMATE_NORMS / 4_ESTIMATE_DATASHEET", null, $"package={packageCode}",
                excluded: null, rowIds: rowIds),
        };
    }

    private const string EstimateStressVersion = "v1";

    // ── idea-5 surface: variance attribution bridge (the "why" behind a flag) ──

    [Description("Explain WHY a cost centre is over/under budget: attribute its cost variance (CV = EV - AC) " +
                 "to the dominant resource category (manpower/material/equipment/subcontract) using estimate " +
                 "shares, and report the schedule lane (SV = EV - PV). This is an ATTRIBUTION, not a proven " +
                 "cause — the named driver is a hypothesis; state the assumption badge and what evidence " +
                 "would confirm it. Only diagnoses live (EV>0) EP- packages.")]
    public object ExplainVariance(
        [Description("Cost centre id")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!PresentPeriod(periodId)) return Err($"periodId must be {_snapshot.MinPeriod}..{_snapshot.ForecastPeriod}.");

        var b = new VarianceAttributor().Attribute(Panel, _snapshot.ResourceMix, bccId, periodId);
        if (!b.Available) return new { bccId, periodId, available = false, reason = b.UnavailableReason };

        return new
        {
            bccId, periodId,
            cvAed = b.CvAed, svAed = b.SvAed, spi = b.Spi,
            dominantResource = b.DominantResourceType,
            resourceBreakdownAvailable = b.ResourceBreakdownAvailable,
            assumptionBased = b.AssumptionBased,
            evidenceNeeded = b.EvidenceNeeded,
            unexplainedResidual = b.UnexplainedResidual,
            tiesOut = b.TiesOut,
            contributions = b.Contributions.Select(c => new
            {
                c.ResourceType, evR = c.EvR, acR = c.AcR, cvR = c.CvR, timesNormBudget = c.TimesNormBudget,
            }),
            sources = Src("9_HISTORICAL_DATA + estimate resource mix", periodId,
                b.Package is null ? bccId : $"{bccId} · {b.Package}", excluded: null,
                rowIds: new[] { Key(bccId, periodId) }),
        };
    }

    /// <summary>
    /// Share of a zone's BAC that must have been spent before its CPI is worth quoting. Mirrors
    /// ModelController's floor exactly — if the assistant and the 3D view disagreed about which
    /// zones are judgeable, one of them would be lying to the same QS.
    /// </summary>
    private const double ZoneMaterialityFloor = 0.01;

    [Description("Locate cost risk PHYSICALLY: rank the building's zones (STRUCTURE, FLOORS-ALL, " +
                 "BASEMENT, …) by how much budget is still unspent in zones that are drifting. " +
                 "Use for 'where are we losing money', 'which part of the building is in trouble', " +
                 "or any question about WHERE rather than which cost centre.")]
    public object LocateCostRisk(
        [Description("Reporting period, 4..12")] int periodId,
        [Description("How many zones to return, 1..10")] int topK = 5)
    {
        if (!PresentPeriod(periodId)) return Err($"periodId must be {_snapshot.MinPeriod}..{_snapshot.ForecastPeriod}.");
        topK = Math.Clamp(topK, 1, 10);

        var rows = Panel.Where(p => p.PeriodId == periodId).ToList();
        var located = rows.Where(p => !string.IsNullOrWhiteSpace(p.ZoneArea)).ToList();
        if (located.Count == 0)
            return Err("This project's data carries no Zone_Area, so cost cannot be located in the building.");

        var zones = located
            .GroupBy(p => p.ZoneArea!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                double bac = g.Sum(p => p.BacAed ?? 0);
                double ev = g.Sum(p => p.EvAed ?? 0);
                double ac = g.Sum(p => p.AcCumulative ?? 0);

                // Aggregate CPI, never a mean of per-centre ratios.
                bool sufficient = ac > 0 && bac > 0 && ac / bac >= ZoneMaterialityFloor;
                double? cpi = sufficient ? ev / ac : null;

                var worst = g.Where(p => p.Cpi is double c && double.IsFinite(c) && (p.AcCumulative ?? 0) > 0)
                             .OrderBy(p => p.Cpi!.Value).FirstOrDefault();

                return new
                {
                    zone = g.Key,
                    bac = Round(bac),
                    ac = Round(ac),
                    unspent = Round(bac - ac),
                    cpi = Round(cpi),
                    judgeable = sufficient,
                    drifting = sufficient && cpi < EvmThresholds.CpiThreshold,
                    centreCount = g.Count(),
                    amberCount = g.Count(p => string.Equals(p.AlertLevel, "AMBER", StringComparison.OrdinalIgnoreCase)),
                    worstCentre = worst?.BccId,
                    worstCentreCpi = Round(worst?.Cpi),
                };
            })
            .OrderByDescending(z => z.drifting)          // drifting zones first…
            .ThenByDescending(z => z.unspent)            // …then by money still at stake
            .Take(topK)
            .ToList();

        double unspentInDrifting = zones.Where(z => z.drifting).Sum(z => z.unspent);

        return new
        {
            periodId,
            zones,
            unspentInDriftingZones = Round(unspentInDrifting),
            // Volunteered so the model cannot present a zone rollup as the whole truth: a zone can
            // sit above 0.95 while the centres inside it are AMBER.
            note = "A zone's CPI is the aggregate of its centres; a zone can read healthy while "
                 + "individual centres inside it are AMBER — check amberCount, not just cpi. Zones "
                 + "below the materiality floor report judgeable=false and no CPI.",
            sources = Src("9_HISTORICAL_DATA grouped by Zone_Area", periodId,
                $"period {periodId} · top {topK} zones", excluded: rows.Count - located.Count,
                rowIds: zones.Select(z => $"ZONE:{z.zone}@P{periodId}")),
        };
    }

    // ── helpers ──

    private static CopilotSources Src(string sheet, int? period, string? filter, int? excluded, IEnumerable<string> rowIds) =>
        new(sheet, period, filter, excluded, rowIds.Distinct(StringComparer.Ordinal).ToList());

    private static object Err(string message) => new { error = message };
    private static double? Fin(double? v) => v is double d && double.IsFinite(d) ? d : (double?)null;
    private static double? Round(double? v) => v is null || !double.IsFinite(v.Value) ? null : Math.Round(v.Value, 3);
    private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 3) : 0;
}
