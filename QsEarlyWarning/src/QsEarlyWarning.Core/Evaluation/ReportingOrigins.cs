using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Evaluation;

/// <summary>
/// The rolling-origin schedule for one project, DERIVED from the data rather than hard-coded
/// (plan §5b, codex Finding 1). This is what makes the analytics project- and period-dynamic:
/// the forecast origin is the latest present reporting period, not a compile-time constant 12, so a
/// project with periods 1..8 (or a new period 13) trains and forecasts correctly.
///
///   ForecastPeriod    = the latest present period (scored with no successor)
///   LastLabeledPeriod = the latest present period that HAS a present successor (can form a label)
///   FirstOrigin       = the first rolling origin scored — needs a minimum of prior history
///                       (EvmThresholds.MinTrainOrigin periods before it, measured from the first period)
///
/// For Tower X (periods 1..12) this yields FirstOrigin=4, LastLabeledPeriod=11, ForecastPeriod=12 —
/// identical to the previous constants.
/// </summary>
public sealed record ReportingOrigins
{
    public required IReadOnlyList<int> Periods { get; init; }
    public required int FirstOrigin { get; init; }
    public required int LastLabeledPeriod { get; init; }
    public required int ForecastPeriod { get; init; }

    public static ReportingOrigins FromPanel(IReadOnlyList<CostCentrePeriod> panel)
        => FromPeriods(panel.Select(p => p.PeriodId));

    public static ReportingOrigins FromPeriods(IEnumerable<int> periodIds)
    {
        var periods = periodIds.Distinct().OrderBy(x => x).ToList();
        if (periods.Count == 0)
            throw new ArgumentException("Cannot derive reporting origins from an empty panel.", nameof(periodIds));

        var present = periods.ToHashSet();
        int forecast = periods[^1];
        // latest period with a present successor; falls back to the forecast period if none (degenerate single period)
        int lastLabeled = periods.Where(p => present.Contains(p + 1)).DefaultIfEmpty(forecast).Max();
        // first scored origin needs (MinTrainOrigin - 1) prior periods of history, relative to the first present period
        int firstOrigin = periods[0] + (EvmThresholds.MinTrainOrigin - 1);

        return new ReportingOrigins
        {
            Periods = periods,
            FirstOrigin = firstOrigin,
            LastLabeledPeriod = lastLabeled,
            ForecastPeriod = forecast,
        };
    }
}
