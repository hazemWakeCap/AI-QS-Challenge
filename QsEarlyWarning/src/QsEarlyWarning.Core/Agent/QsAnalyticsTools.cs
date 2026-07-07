using System.ComponentModel;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Domain.ValueObjects;

namespace QsEarlyWarning.Core.Agent;

/// <summary>
/// Read-only analytics tools the copilot can call (plan §6.8). Every method is side-effect-free
/// and validates/clamps its own args, returning a typed error string rather than throwing — the
/// tool boundary is the real authorization surface. All backed by WatchlistScoringService + the
/// model snapshot, so the copilot can never diverge from the watchlist.
/// </summary>
public sealed class QsAnalyticsTools
{
    private readonly IModelProvider _provider;
    private readonly WatchlistScoringService _scoring;

    public QsAnalyticsTools(IModelProvider provider, WatchlistScoringService scoring)
    {
        _provider = provider;
        _scoring = scoring;
    }

    private static bool ValidPeriod(int p) => p is >= EvmThresholds.MinTrainOrigin and <= EvmThresholds.ForecastPeriod;

    [Description("Get the ranked watchlist of GREEN cost centres most at risk of tipping to AMBER next " +
                 "period. periodId must be 4..12 (12 is the live forecast). topK is 5 or 10. Returns each " +
                 "centre's risk score, CPI, budget/progress gap (percentage points), and plain-language " +
                 "risk indicators.")]
    public object GetWatchlist(
        [Description("Reporting period, 4..12 (12 = live forecast)")] int periodId,
        [Description("How many top centres to return: 5 or 10")] int topK = 10)
    {
        if (!ValidPeriod(periodId)) return Err($"periodId must be {EvmThresholds.MinTrainOrigin}..{EvmThresholds.ForecastPeriod}.");
        if (topK is not (5 or 10)) topK = 10;

        var s = _provider.Current;
        var r = _scoring.ScorePeriod(s.Panel, periodId, s.Model);
        if (r.Status == ScoreStatus.NoArtifact) return Err($"No model serves period {periodId}.");

        return new
        {
            period = periodId,
            isForecast = r.IsForecast,
            eligibleCount = r.Rows.Count,
            rows = r.Rows.Take(topK).Select((row, i) => new
            {
                rank = i + 1,
                bccId = row.BccId,
                discipline = row.Discipline,
                riskScore = Math.Round(row.RiskScore, 3),
                cpi = Math.Round(row.Cpi, 3),
                gapPp = Math.Round(row.Gap, 2),
                riskIndicators = row.RiskIndicators,
            }),
        };
    }

    [Description("Get one cost centre's recent EVM + trend history across periods (CPI, gap, alert level). " +
                 "Use to explain why a centre is or isn't on the watchlist.")]
    public object GetCostCentreDetail(
        [Description("Cost centre id, e.g. BCC-ARC-PAINT-317")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!ValidPeriod(periodId)) return Err($"periodId must be {EvmThresholds.MinTrainOrigin}..{EvmThresholds.ForecastPeriod}.");

        var rows = _provider.Current.Panel
            .Where(p => string.Equals(p.BccId, bccId, StringComparison.OrdinalIgnoreCase) && p.PeriodId <= periodId)
            .OrderBy(p => p.PeriodId)
            .TakeLast(4)
            .Select(p => new { period = p.PeriodId, alert = p.AlertLevel, cpi = p.Cpi, gapPp = p.Gap, spi = p.Spi })
            .ToList();

        return rows.Count == 0 ? Err($"No rows for {bccId} at/under period {periodId}.")
            : new { bccId, history = rows };
    }

    [Description("Explain why a cost centre received its risk score at a period: the deterministic risk " +
                 "indicators (which threshold/trend conditions put it high).")]
    public object ExplainDrift(
        [Description("Cost centre id")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!ValidPeriod(periodId)) return Err($"periodId must be {EvmThresholds.MinTrainOrigin}..{EvmThresholds.ForecastPeriod}.");

        var s = _provider.Current;
        var r = _scoring.ScorePeriod(s.Panel, periodId, s.Model);
        var row = r.Rows.FirstOrDefault(x => string.Equals(x.BccId, bccId, StringComparison.OrdinalIgnoreCase));
        return row is null
            ? Err($"{bccId} is not a scoreable GREEN centre at period {periodId}.")
            : new { bccId, periodId, riskScore = Math.Round(row.RiskScore, 3), riskIndicators = row.RiskIndicators };
    }

    [Description("Get EVM identities (CV, CPI, SPI, EAC, VAC) for a cost centre at a period, from recorded " +
                 "workbook values. Never fabricates the withheld budget/EV sheets.")]
    public object GetEvmSnapshot(
        [Description("Cost centre id")] string bccId,
        [Description("Reporting period, 4..12")] int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId)) return Err("bccId is required.");
        if (!ValidPeriod(periodId)) return Err($"periodId must be {EvmThresholds.MinTrainOrigin}..{EvmThresholds.ForecastPeriod}.");

        var row = _provider.Current.Panel.FirstOrDefault(p =>
            string.Equals(p.BccId, bccId, StringComparison.OrdinalIgnoreCase) && p.PeriodId == periodId);
        if (row is null) return Err($"No row for {bccId} at period {periodId}.");

        var e = EvmSnapshot.From(row);
        return new
        {
            bccId, periodId,
            cv = Round(e.Cv), cpi = Round(e.Cpi), spi = Round(e.Spi),
            eacRecorded = Round(e.EacRecorded), eacCpiMethod = Round(e.EacCpiMethod), vac = Round(e.Vac),
        };
    }

    private static object Err(string message) => new { error = message };
    private static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 3);
}
