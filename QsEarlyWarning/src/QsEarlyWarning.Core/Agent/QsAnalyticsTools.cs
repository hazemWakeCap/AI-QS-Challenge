using System.ComponentModel;
using QsEarlyWarning.Core.Forecasting;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Core.Scoring;
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

    [Description("Explain why a cost centre received its risk score at a period: the deterministic risk " +
                 "indicators (which threshold/trend conditions put it high).")]
    public object ExplainDrift(
        [Description("Cost centre id")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!ScoreablePeriod(periodId)) return Err($"periodId must be {EvmThresholds.MinTrainOrigin}..{EvmThresholds.ForecastPeriod}.");

        var r = _scoring.ScorePeriod(Panel, periodId, _snapshot.Model);
        var row = r.Rows.FirstOrDefault(x => string.Equals(x.BccId, bccId, StringComparison.OrdinalIgnoreCase));
        return row is null
            ? Err($"{bccId} is not a scoreable GREEN centre at period {periodId}.")
            : new
            {
                bccId, periodId,
                riskScore = Math.Round(row.RiskScore, 3),
                riskIndicators = row.RiskIndicators,
                sources = Src("9_HISTORICAL_DATA", periodId, bccId, excluded: null, rowIds: new[] { Key(bccId, periodId) }),
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

    // ── helpers ──

    private static CopilotSources Src(string sheet, int? period, string? filter, int? excluded, IEnumerable<string> rowIds) =>
        new(sheet, period, filter, excluded, rowIds.Distinct(StringComparer.Ordinal).ToList());

    private static object Err(string message) => new { error = message };
    private static double? Round(double? v) => v is null || !double.IsFinite(v.Value) ? null : Math.Round(v.Value, 3);
    private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 3) : 0;
}
