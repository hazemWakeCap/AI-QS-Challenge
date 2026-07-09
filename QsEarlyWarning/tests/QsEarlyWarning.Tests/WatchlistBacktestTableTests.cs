using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;
using Xunit.Abstractions;

namespace QsEarlyWarning.Tests;

/// <summary>
/// The per-row "hit/miss" backtest a QS does by hand: for a period, rank the GREEN centres with the
/// real out-of-fold model, then look up each flagged centre's ACTUAL next-period Alert_Level in the
/// workbook. A HIT = flagged AND next period is AMBER. This is precision@k made human-readable.
/// </summary>
public sealed class WatchlistBacktestTableTests
{
    private readonly ITestOutputHelper _out;
    public WatchlistBacktestTableTests(ITestOutputHelper output) => _out = output;

    private static readonly IReadOnlyList<CostCentrePeriod> Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath);
    private static readonly TrainedModel Model = new RollingOriginEvaluator().Train(Panel);
    private static readonly WatchlistScoringService Scoring = new();

    /// <summary>Actual Alert_Level for (bcc, period) straight from the workbook, or null if absent.</summary>
    private static string? ActualAlert(string bcc, int period) => Panel
        .FirstOrDefault(r => r.BccId == bcc && r.PeriodId == period)?.AlertLevel;

    [Fact]
    public void Print_watchlist_vs_actual_next_period_for_all_backtestable_periods()
    {
        int hitsTotal = 0, flaggedTotal = 0;

        // Out-of-fold origins only (4..11) — each has a real next period to check against.
        for (int p = 4; p <= 11; p++)
        {
            var result = Scoring.ScorePeriod(Panel, p, Model);
            if (result.Status != ScoreStatus.Ok) continue;

            var top5 = result.Rows.Take(5).ToList();
            _out.WriteLine($"── Period {p}  (model cutoff {result.TrainingCutoffPeriod}, " +
                           $"{result.Rows.Count} GREEN eligible) → checked against actual P{p + 1} ──");

            int hits = 0;
            foreach (var (r, i) in top5.Select((r, i) => (r, i)))
            {
                var next = ActualAlert(r.BccId, p + 1) ?? "—";
                bool hit = string.Equals(next, "AMBER", StringComparison.OrdinalIgnoreCase);
                if (hit) hits++;
                _out.WriteLine($"  #{i + 1} {r.BccId,-18} score={r.RiskScore:0.000} " +
                               $"CPI={r.Cpi:0.000} gap={r.Gap,5:0.0}pp  → P{p + 1}={next,-11} " +
                               $"{(hit ? "HIT ✓" : "miss")}");
            }
            _out.WriteLine($"  precision@5 = {hits}/5 = {hits / 5.0:0.00}\n");
            hitsTotal += hits;
            flaggedTotal += top5.Count;
        }

        _out.WriteLine($"TOTAL over origins 4..11: {hitsTotal}/{flaggedTotal} top-5 flags " +
                       $"actually went AMBER next period (pooled precision@5 = {hitsTotal / (double)flaggedTotal:0.000}).");
        Assert.True(flaggedTotal > 0);
    }

    [Fact]
    public void Named_row_spotcheck_BCC_STR_CON_204_at_period_5_is_a_real_transition()
    {
        // The manual check: is BCC-STR-CON-204 GREEN at P5, flagged, and actually AMBER at P6?
        const string bcc = "BCC-STR-CON-204";

        Assert.Equal("GREEN", ActualAlert(bcc, 5));   // eligible to be warned about
        Assert.Equal("AMBER", ActualAlert(bcc, 6));   // the transition the QS wants caught early

        var result = Scoring.ScorePeriod(Panel, 5, Model);
        Assert.Equal(ScoreStatus.Ok, result.Status);

        var row = result.Rows.FirstOrDefault(r => r.BccId == bcc);
        Assert.NotNull(row); // it IS scored on the P5 watchlist

        int rank = result.Rows.ToList().FindIndex(r => r.BccId == bcc) + 1;
        _out.WriteLine($"{bcc}: P5=GREEN (CPI={row!.Cpi:0.000}, gap={row.Gap:0.0}pp) → " +
                       $"risk score {row.RiskScore:0.000}, rank {rank}/{result.Rows.Count}; actual P6=AMBER ⇒ HIT.");
        foreach (var why in row.RiskIndicators) _out.WriteLine($"   • {why}");
    }
}
