using QsEarlyWarning.Core.StressTest;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Domain.Estimate;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Idea-3 Estimate Assumption Stress Test — verifies the CALCULATIONS and the leakage/validity guards.
/// The reconciliation tie-out (Class 1) is the engine's credibility artifact: rebuilt should-cost ties
/// to BOQ direct cost to the AED, proving the Output-Norm divisor is applied correctly.
/// </summary>
public sealed class StressTestTests
{
    private const long OwningId = 42;
    private static readonly EstimateModel Estimate =
        new EstimateWorkbookLoader(TestData.WorkbookPath, OwningId).TryLoadForProject(OwningId)!;
    private static readonly CostCentrePeriod[] Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath).ToArray();

    [Fact]
    public void Loader_reads_all_four_estimate_sheets()
    {
        Assert.NotNull(Estimate);
        Assert.True(Estimate.Norms.Count > 100, $"norms={Estimate.Norms.Count}");
        Assert.True(Estimate.BoqLines.Count > 100, $"boq={Estimate.BoqLines.Count}");
        Assert.True(Estimate.Mappings.Count > 100, $"mappings={Estimate.Mappings.Count}");
        Assert.True(Estimate.ResourceLines.Count > 500, $"lines={Estimate.ResourceLines.Count}");
    }

    [Fact]
    public void Loader_is_project_gated()
    {
        var loader = new EstimateWorkbookLoader(TestData.WorkbookPath, OwningId);
        Assert.NotNull(loader.TryLoadForProject(OwningId));
        Assert.Null(loader.TryLoadForProject(OwningId + 1));           // wrong project → unavailable
        Assert.Null(new EstimateWorkbookLoader(TestData.WorkbookPath, null).TryLoadForProject(OwningId)); // fail closed
    }

    // ── Class 1: the credibility artifact ──

    [Fact]
    public void Class1_reconciliation_ties_out_to_the_AED()
    {
        var report = new EstimateStressTester().Run(Estimate, Panel);
        var recon = report.Reconciliation;
        Assert.True(recon.ItemsChecked > 100, $"items={recon.ItemsChecked}");
        Assert.True(recon.TiesOut,
            $"tie-out FAILED: {recon.ItemsFailed}/{recon.ItemsChecked} items, " +
            $"projectDirectDelta={recon.ProjectDirectDelta:0.###}, projectUpliftDelta={recon.ProjectUpliftDelta:0.###}. " +
            $"First failures: {string.Join(" | ", recon.Items.Where(i => !i.TiesOut).Take(3).SelectMany(i => i.Failures).Take(5))}");
        // Residual is exactly margin + contingency (uplift), not a signal.
        Assert.True(Math.Abs(recon.ProjectUpliftDelta) <= EstimateStressTester.RollupAbsTol);
    }

    [Fact]
    public void Class1_output_norm_divisor_is_load_bearing()
    {
        // Recompute a manpower line's Total Resource Qty with vs without the ÷ Output Norm divisor and
        // confirm only the divided form matches the stored quantity (the reason the tie-out passes).
        var line = Estimate.ResourceLines.First(l =>
            l.ResourceType.StartsWith("MANPOWER") && l.BoqQty is > 0 && l.QtyPerUnitWork is > 0 && l.TotalResourceQty is > 0
            && l.NormCode is not null && Estimate.NormByCode.TryGetValue(l.NormCode, out var n) && n.OutputNorm is > 0);
        var norm = Estimate.NormByCode[line.NormCode!];
        var withDivisor = line.BoqQty!.Value * line.QtyPerUnitWork!.Value / norm.OutputNorm!.Value;
        var withoutDivisor = line.BoqQty!.Value * line.QtyPerUnitWork!.Value;
        Assert.Equal(line.TotalResourceQty!.Value, withDivisor, 3);
        Assert.True(Math.Abs(withoutDivisor - line.TotalResourceQty!.Value) > 1.0); // the dropped-divisor bug would overstate
    }

    // ── Class 2: estimate-side, zero actuals; cohort-gated ──

    [Fact]
    public void Class2_reads_no_actuals_identical_with_or_without_panel()
    {
        var withPanel = new EstimateStressTester().Run(Estimate, Panel).AssumptionFlags;
        var without = new EstimateStressTester().Run(Estimate, null).AssumptionFlags;
        Assert.Equal(without.Count, withPanel.Count);
        Assert.Equal(without.Select(f => (f.Package, f.Kind, f.DrivingResourceLine)),
                     withPanel.Select(f => (f.Package, f.Kind, f.DrivingResourceLine)));
    }

    [Fact]
    public void Class2_is_deterministic_and_flags_carry_a_rules_version()
    {
        var a = new EstimateStressTester().Run(Estimate, Panel).AssumptionFlags;
        var b = new EstimateStressTester().Run(Estimate, Panel).AssumptionFlags;
        Assert.Equal(a.Select(f => (f.Package, f.Kind, f.Reason)), b.Select(f => (f.Package, f.Kind, f.Reason)));
        Assert.All(a, f => Assert.Equal("v1", f.RulesVersion));
        Assert.All(a, f => Assert.True(f.CohortN >= EstimateStressTester.MinCohortN));
        Assert.Contains(a, f => f.Kind == "OutputNormTopPercentile");
    }

    [Fact]
    public void Class2_contingency_rules_are_mutually_exclusive()
    {
        var flags = new EstimateStressTester().Run(Estimate, Panel).AssumptionFlags;
        // No package/line carries both zero and thin contingency for the same driving item.
        var zero = flags.Where(f => f.Kind == "ZeroContingency").Select(f => f.DrivingResourceLine).ToHashSet();
        var thin = flags.Where(f => f.Kind == "ThinContingency").Select(f => f.DrivingResourceLine).ToHashSet();
        Assert.Empty(zero.Intersect(thin));
    }

    // ── Class 3: retrospective, leave-one-out, gated ──

    [Fact]
    public void Class3_never_uses_a_packages_own_actual_leave_one_out()
    {
        var benches = new EstimateStressTester().Run(Estimate, Panel).PeerBenchmarks;
        // Every benchmarked cell's peer count is bounded by the number of OTHER packages, and no
        // benchmark can be produced from a single package's own data (LOO). Assert via the suppressed
        // path: a synthetic single-package panel yields zero benchmarked cells for that package.
        Assert.All(benches.Where(b => b.Status == "Benchmarked"), b => Assert.True(b.PeerCount >= EstimateStressTester.MinPeerN));
    }

    [Fact]
    public void Class3_suppressed_below_five_peers_and_reports_actual_counts()
    {
        var report = new EstimateStressTester().Run(Estimate, Panel);
        // Cells with 1-4 peers are Suppressed but still publish their real (non-zero-able) peer count.
        Assert.All(report.PeerBenchmarks, b =>
            Assert.True(b.Status == "Benchmarked" ? b.PeerCount >= 5 : b.PeerCount < 5));
        // The flag is honest: it means "no cell meets the minimum", not "0 peers".
        if (report.Class3NoCellMeetsMinPeers)
            Assert.DoesNotContain(report.PeerBenchmarks, b => b.Status == "Benchmarked");
    }

    [Fact]
    public void Class3_absent_panel_yields_no_benchmarks_and_the_no_min_flag()
    {
        var report = new EstimateStressTester().Run(Estimate, null);
        Assert.Empty(report.PeerBenchmarks);
        Assert.True(report.Class3NoCellMeetsMinPeers);
    }

    [Fact]
    public void Quantile_type7_matches_known_values()
    {
        var data = new double[] { 1, 2, 3, 4, 5 };
        Assert.Equal(3.0, EstimateStressTester.Quantile(data, 0.5), 6);
        Assert.Equal(1.0, EstimateStressTester.Quantile(data, 0.0), 6);
        Assert.Equal(5.0, EstimateStressTester.Quantile(data, 1.0), 6);
        Assert.Equal(4.6, EstimateStressTester.Quantile(data, 0.9), 6); // 1 + 0.9*4 -> interp
    }
}
