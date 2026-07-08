using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Core.Variance;
using QsEarlyWarning.Domain.Entities;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Idea-5 Variance Attribution Bridge — verifies the EVM identities and the honesty guards. The tie-out
/// (Σ CVr + unexplained residual = CV, and SV = EV − PV) is the trust anchor; attribution is by resource,
/// never a quantity/price split of CV.
/// </summary>
public sealed class VarianceTests
{
    private static readonly ProjectSnapshot Snap = TestSnapshot.Build();
    private static readonly VarianceAttributor Engine = new();

    // A live, EP-, fully-figured cost-centre row to diagnose.
    private static CostCentrePeriod LiveRow() => Snap.Panel.First(p =>
        p.PeriodId == 8 && p.PackageCode.StartsWith("EP-") && p.EvAed is > 0
        && p.AcCumulative is double && p.PvAed is double);

    [Fact]
    public void Resource_mix_is_present_for_the_owning_project()
    {
        Assert.NotNull(Snap.ResourceMix);
        Assert.True(Snap.ResourceMix!.Count > 50, $"packages={Snap.ResourceMix.Count}");
    }

    [Fact]
    public void Bridge_ties_out_to_the_AED()
    {
        var row = LiveRow();
        var b = Engine.Attribute(Snap.Panel, Snap.ResourceMix, row.BccId, 8);
        Assert.True(b.Available);
        Assert.True(b.ResourceBreakdownAvailable);
        // CV identity matches the recorded value.
        Assert.Equal(row.EvAed!.Value - row.AcCumulative!.Value, b.CvAed!.Value, 2);
        // SV lane = EV − PV.
        Assert.Equal(row.EvAed!.Value - row.PvAed!.Value, b.SvAed!.Value, 2);
        // Tie-out: Σ CVr + unexplained residual == CV.
        var sumCvr = b.Contributions.Sum(c => c.CvR);
        Assert.Equal(b.CvAed!.Value, sumCvr + b.UnexplainedResidual!.Value, 1);
        Assert.True(b.TiesOut);
    }

    [Fact]
    public void Dominant_contributor_follows_the_variance_direction_and_residual_rule()
    {
        var row = LiveRow();
        var b = Engine.Attribute(Snap.Panel, Snap.ResourceMix, row.BccId, 8);
        // Recompute the expected dominant from the bridge's own outputs (the selection is deterministic).
        var over = b.CvAed!.Value < 0;
        var top = over ? b.Contributions.OrderBy(c => c.CvR).First()
                       : b.Contributions.OrderByDescending(c => c.CvR).First();
        var expected = Math.Abs(b.UnexplainedResidual!.Value) > Math.Abs(top.CvR) ? "unexplained residual" : top.ResourceType;
        Assert.Equal(expected, b.DominantResourceType);
    }

    [Fact]
    public void Attribution_is_flagged_assumption_based_with_evidence_needed()
    {
        var row = LiveRow();
        var b = Engine.Attribute(Snap.Panel, Snap.ResourceMix, row.BccId, 8);
        Assert.True(b.AssumptionBased);
        Assert.False(string.IsNullOrWhiteSpace(b.EvidenceNeeded));
        Assert.Contains(b.Notes, n => n.Contains("assumption", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Non_EP_row_is_unavailable()
    {
        var synthetic = new[] { new CostCentrePeriod
        {
            PeriodId = 8, BccId = "BCC-FAKE", PackageCode = "ZZ-NOT-EP",
            EvAed = 100, AcCumulative = 90, PvAed = 100,
        }};
        var b = Engine.Attribute(synthetic, Snap.ResourceMix, "BCC-FAKE", 8);
        Assert.False(b.Available);
        Assert.Contains("EP-", b.UnavailableReason);
    }

    [Fact]
    public void Missing_money_or_not_started_row_is_unavailable_no_throw()
    {
        var notStarted = new[] { new CostCentrePeriod
        {
            PeriodId = 8, BccId = "BCC-NS", PackageCode = "EP-CIV-DEMO",
            EvAed = 0, AcCumulative = 0, PvAed = 100,   // EV = 0 → not diagnosable
        }};
        var b = Engine.Attribute(notStarted, Snap.ResourceMix, "BCC-NS", 8);
        Assert.False(b.Available);
    }

    [Fact]
    public void Mix_absent_still_gives_CV_and_SV_totals_without_resource_breakdown()
    {
        var row = LiveRow();
        var b = Engine.Attribute(Snap.Panel, mix: null, row.BccId, 8);
        Assert.True(b.Available);
        Assert.False(b.ResourceBreakdownAvailable);
        Assert.Empty(b.Contributions);
        Assert.NotNull(b.CvAed);
        Assert.NotNull(b.SvAed);
    }
}
