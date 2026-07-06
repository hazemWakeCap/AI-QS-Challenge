using QsEarlyWarning.Core.Scoring;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>Guards the frozen RuleRiskScore@v1 — especially the CPI-proximity direction (plan §6.4/§6.11).</summary>
public sealed class RuleScoreTests
{
    private const double CpiBand = 0.10;

    private static RuleArtifact Artifact(double xStar = 1.0, double gapScale = 5.0) => new()
    {
        TrainingCutoffPeriod = 5, Role = ArtifactRole.Oof,
        XStar = xStar, GapScale = gapScale, TrainingPrefixFingerprint = "test",
    };

    [Fact]
    public void CpiProximity_is_one_at_the_boundary()
        => Assert.Equal(1.0, RuleScorer.CpiProximity(0.95, CpiBand), 9);

    [Fact]
    public void CpiProximity_between_zero_and_one_just_above_boundary()
    {
        var v = RuleScorer.CpiProximity(0.96, CpiBand);
        Assert.True(v is > 0.0 and < 1.0, $"expected (0,1) got {v}");
    }

    [Fact]
    public void CpiProximity_zero_at_and_above_band_edge()
    {
        Assert.Equal(0.0, RuleScorer.CpiProximity(0.95 + CpiBand, CpiBand), 9);
        Assert.Equal(0.0, RuleScorer.CpiProximity(1.20, CpiBand), 9); // clamped, never negative
    }

    [Fact]
    public void CpiProximity_is_monotone_non_increasing_as_cpi_rises()
    {
        double prev = double.PositiveInfinity;
        for (double cpi = 0.95; cpi <= 1.30; cpi += 0.01)
        {
            var v = RuleScorer.CpiProximity(cpi, CpiBand);
            Assert.True(v <= prev + 1e-12, $"not monotone at cpi={cpi}");
            prev = v;
        }
    }

    [Fact]
    public void Cpi_component_is_not_identically_zero_for_green_population()
    {
        // The regression guard for the previous bug: GREEN rows (cpi>=0.95) must get real CPI signal.
        var a = Artifact();
        double atBoundary = RuleScorer.Score(gap: 0, cpi: 0.95, a); // gap term 0 → pure CPI component
        Assert.True(atBoundary > 0.0, "CPI component vanished for a GREEN row at the boundary");
        Assert.Equal(a.WCpi * 1.0, atBoundary, 9);
    }

    [Fact]
    public void Weights_and_band_are_fixed_v1_constants()
    {
        var a = Artifact();
        Assert.Equal(0.7, a.WGap, 9);
        Assert.Equal(0.3, a.WCpi, 9);
        Assert.Equal(0.10, a.CpiBand, 9);
        Assert.Equal("RuleRiskScore@v1", RuleArtifact.ScorerVersion);
    }
}
