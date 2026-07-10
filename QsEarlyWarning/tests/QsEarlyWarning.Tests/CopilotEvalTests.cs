using QsEarlyWarning.Core.Agent;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Entities;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Idea-4 QS Copilot — the fixed-question ground-truth eval (CI-safe, NO model call). For each question
/// the ground truth is computed INDEPENDENTLY from the panel here (leakage guard G8) and asserted against
/// the deterministic TOOL output. Proves tool correctness, the sum(EV)/sum(AC) & sum(EV)/sum(PV)
/// aggregation rules (never mean-of-rows), the validated-vs-directional boundary, argument validation,
/// and the adversarial cases. The live-LLM comparison lives in <c>CopilotLiveEvalTests</c> (opt-in).
/// </summary>
public sealed class CopilotEvalTests
{
    private static readonly IReadOnlyList<CostCentrePeriod> Panel = TestSnapshot.Build().Panel;
    private static readonly QsAnalyticsTools Tools = new(TestSnapshot.Build(), new WatchlistScoringService());

    // ── reflection helpers over the tools' anonymous return objects ──
    private static object? P(object? o, string name) => o?.GetType().GetProperty(name)?.GetValue(o);
    private static double? D(object? o) => o switch { double d => d, int i => i, _ => null };
    private static string? Err(object o) => P(o, "error") as string;

    // ── numeric exact-match: per-centre EVM ──

    [Fact]
    public void EvmSnapshot_cpi_matches_independently_computed_EV_over_AC()
    {
        var row = Panel.First(p => p.PeriodId == 8 && p.EvAed is double && p.AcCumulative is double a && a > 0);
        var gtCpi = Math.Round(row.EvAed!.Value / row.AcCumulative!.Value, 3);

        var result = Tools.GetEvmSnapshot(row.BccId, 8);
        Assert.Null(Err(result));
        Assert.Equal(gtCpi, D(P(result, "cpi")));
        // G7: the source row key is the natural composite key.
        var sources = P(result, "sources") as CopilotSources;
        Assert.Contains($"{row.BccId}@P8", sources!.RowIds);
    }

    [Fact]
    public void EvmSnapshot_exposes_no_final_cost_field_G3()
    {
        var row = Panel.First(p => p.PeriodId == 8 && p.Cpi is double);
        var result = Tools.GetEvmSnapshot(row.BccId, 8);
        foreach (var banned in new[] { "eac", "eacRecorded", "eacCpiMethod", "vac", "finalCost" })
            Assert.Null(result.GetType().GetProperty(banned));
    }

    // ── aggregation trap: sum(EV)/sum(AC) and sum(EV)/sum(PV), never mean-of-rows ──

    [Fact]
    public void ProjectEvm_cpi_is_sumEV_over_sumAC_not_mean_of_rows()
    {
        const int period = 8;
        var eligible = Panel.Where(p => p.PeriodId == period
            && p.EvAed is double && p.AcCumulative is double a && a > 0).ToList();
        var gtAgg = Math.Round(eligible.Sum(p => p.EvAed!.Value) / eligible.Sum(p => p.AcCumulative!.Value), 3);
        var meanOfRows = Math.Round(eligible.Average(p => p.EvAed!.Value / p.AcCumulative!.Value), 3);

        var cpi = P(Tools.ProjectEvm(period), "cpi");
        Assert.True((bool)P(cpi, "available")!);
        Assert.Equal(gtAgg, D(P(cpi, "value")));
        Assert.NotEqual(meanOfRows, gtAgg);                       // the trap: the two forms differ
        Assert.Equal(eligible.Count, (int)P(cpi, "includedCount")!);
    }

    [Fact]
    public void ProjectEvm_spi_is_sumEV_over_sumPV_not_sumEV_over_sumAC()
    {
        const int period = 8;
        var eligible = Panel.Where(p => p.PeriodId == period
            && p.EvAed is double && p.PvAed is double v && v > 0).ToList();
        var gtSpi = Math.Round(eligible.Sum(p => p.EvAed!.Value) / eligible.Sum(p => p.PvAed!.Value), 3);

        var spi = P(Tools.ProjectEvm(period), "spi");
        Assert.Equal(gtSpi, D(P(spi, "value")));
        // SPI denominator is PV, not AC — the two aggregates differ on this panel.
        var acAgg = eligible.Sum(p => p.EvAed!.Value) / eligible.Sum(p => p.AcCumulative!.Value);
        Assert.NotEqual(Math.Round(acAgg, 3), gtSpi);
    }

    [Fact]
    public void ProjectEvm_keeps_zero_EV_positive_AC_rows_in_the_CPI_sum_G4()
    {
        // The CPI eligibility keys on the denominator (AC>0) only — a zero-EV row must NOT be dropped.
        const int period = 8;
        int gtIncluded = Panel.Count(p => p.PeriodId == period
            && p.EvAed is double && p.AcCumulative is double a && a > 0);   // EV may be zero
        var cpi = P(Tools.ProjectEvm(period), "cpi");
        Assert.Equal(gtIncluded, (int)P(cpi, "includedCount")!);
    }

    [Fact]
    public void ProjectEvm_empty_scope_is_available_false_not_divide_by_zero_G4()
    {
        var cpi = P(Tools.ProjectEvm(8, discipline: "NO-SUCH-DISCIPLINE"), "cpi");
        Assert.False((bool)P(cpi, "available")!);
        Assert.Null(P(cpi, "value"));
    }

    // ── validated forecast vs directional EAC (G3) ──

    [Fact]
    public void ForecastIncrementalSpend_returns_validated_bands_and_no_final_cost()
    {
        // pick a centre the forecaster serves
        var bcc = Panel.Select(p => p.BccId).Distinct()
            .FirstOrDefault(id => Err(Tools.ForecastIncrementalSpend(id)) is null);
        Assert.NotNull(bcc);
        var result = Tools.ForecastIncrementalSpend(bcc!);
        Assert.True((bool)P(result, "validated")!);
        Assert.NotNull(P(result, "increments"));
        foreach (var banned in new[] { "eac", "vac", "finalCost", "directionalFinalCost" })
            Assert.Null(result.GetType().GetProperty(banned));
    }

    [Fact]
    public void DirectionalEac_is_flagged_unvalidated_and_returns_eac_vac()
    {
        var row = Panel.First(p => p.PeriodId == 8 && p.BacAed is double && p.Cpi is double c && c > 0);
        var result = Tools.DirectionalEac(row.BccId, 8);
        Assert.False((bool)P(result, "validated")!);
        Assert.NotNull(P(result, "eac"));
        Assert.NotNull(P(result, "vac"));
        // ground truth: EAC = BAC/CPI
        Assert.Equal(Math.Round(row.BacAed!.Value / row.Cpi!.Value, 3), D(P(result, "eac")));
    }

    [Fact]
    public void DirectionalEac_guards_missing_or_nonpositive_cpi()
    {
        var notStarted = Panel.FirstOrDefault(p => p.AlertLevel == "NOT STARTED"
            && (p.Cpi is null || p.AcCumulative is null || p.AcCumulative == 0));
        Assert.NotNull(notStarted);
        var result = Tools.DirectionalEac(notStarted!.BccId, notStarted.PeriodId);
        Assert.False((bool)P(result, "available")!);
        Assert.Null(result.GetType().GetProperty("eac"));
    }

    // ── argument validation / adversarial ──

    [Fact]
    public void Invalid_bcc_returns_typed_error_not_a_guess()
    {
        Assert.NotNull(Err(Tools.GetEvmSnapshot("BCC-DOES-NOT-EXIST", 8)));
        Assert.NotNull(Err(Tools.ForecastIncrementalSpend("BCC-DOES-NOT-EXIST")));
        Assert.NotNull(Err(Tools.ResourceSplit("", 8)));
    }

    [Fact]
    public void Ambiguous_out_of_range_period_is_rejected()
    {
        Assert.Contains("periodId", Err(Tools.GetWatchlist(99, 5))!);
        Assert.NotNull(Err(Tools.ProjectEvm(99)));
    }

    // ── progress: plan/actual percent complete filter ──

    [Fact]
    public void ListCentresByProgress_matches_independently_computed_plan_below_100()
    {
        const int period = 8;
        // Independent ground truth: centres at the period with BOTH progress fields finite and Plan < 100.
        var scoreable = Panel.Where(p => p.PeriodId == period
            && p.PlanPctComplete is double pl && double.IsFinite(pl)
            && p.ActualPctComplete is double a && double.IsFinite(a)).ToList();
        int gtBelow100 = scoreable.Count(p => p.PlanPctComplete!.Value < 100);
        int gtExcluded = Panel.Count(p => p.PeriodId == period) - scoreable.Count;

        var result = Tools.ListCentresByProgress(period, maxPlanPct: 100, limit: 500);
        Assert.Null(Err(result));
        Assert.Equal(gtBelow100, (int)P(result, "matchedCount")!);
        Assert.Equal(gtExcluded, (int)P(result, "excludedCount")!);   // rows missing plan/actual are counted, not dropped
        // Every returned row honours the strict-less-than bound.
        var rows = (System.Collections.IEnumerable)P(result, "rows")!;
        foreach (var row in rows) Assert.True((D(P(row, "planPctComplete")) ?? 100) < 100);
    }

    [Fact]
    public void ListCentresByProgress_rejects_out_of_range_period()
        => Assert.NotNull(Err(Tools.ListCentresByProgress(99)));

    // ── resource split + cross-sheet (stress) ──

    [Fact]
    public void ResourceSplit_shares_sum_to_100_when_ac_present()
    {
        var row = Panel.First(p => p.PeriodId == 8
            && (p.AcManpower ?? 0) + (p.AcMaterial ?? 0) + (p.AcEquipment ?? 0) + (p.AcSubcontract ?? 0) > 0);
        var result = Tools.ResourceSplit(row.BccId, 8);
        var shares = P(result, "sharesPct")!;
        var sum = (D(P(shares, "manpower")) ?? 0) + (D(P(shares, "material")) ?? 0)
                  + (D(P(shares, "equipment")) ?? 0) + (D(P(shares, "subcontract")) ?? 0);
        Assert.InRange(sum, 99.0, 101.0);
    }

    [Fact]
    public void StressFlagsForPackage_filters_by_package_and_cites_item_refs()
    {
        var result = Tools.StressFlagsForPackage("EP-STR-CON");
        Assert.True((bool)P(result, "available")!);
        var sources = P(result, "sources") as CopilotSources;
        Assert.NotEmpty(sources!.RowIds);   // union of flag SourceItemRefs + reconciliation item refs
    }

    [Fact]
    public void StressFlagsForPackage_unknown_package_is_available_with_empty_flags()
    {
        var result = Tools.StressFlagsForPackage("EP-NOT-A-PACKAGE");
        Assert.True((bool)P(result, "available")!);   // estimate exists; just no rows for this package
    }

    // ── idea-5 variance attribution tool ──

    [Fact]
    public void ExplainVariance_returns_an_attribution_with_the_assumption_flag()
    {
        var row = Panel.First(p => p.PeriodId == 8 && p.PackageCode.StartsWith("EP-")
            && p.EvAed is > 0 && p.AcCumulative is double && p.PvAed is double);
        var result = Tools.ExplainVariance(row.BccId, 8);
        Assert.NotNull(P(result, "cvAed"));
        Assert.NotNull(P(result, "svAed"));
        Assert.True((bool)P(result, "assumptionBased")!);
        Assert.NotNull(P(result, "evidenceNeeded"));
        Assert.NotNull(P(result, "dominantResource"));
    }

    [Fact]
    public void ExplainVariance_unknown_or_not_started_is_unavailable_not_a_guess()
    {
        Assert.Null(Err(Tools.ExplainVariance("BCC-DOES-NOT-EXIST", 8)));   // no typed error…
        Assert.False((bool)P(Tools.ExplainVariance("BCC-DOES-NOT-EXIST", 8), "available")!); // …just available:false
    }
}
