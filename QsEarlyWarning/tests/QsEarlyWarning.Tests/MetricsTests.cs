using QsEarlyWarning.Core.Evaluation;
using Xunit;

namespace QsEarlyWarning.Tests;

public sealed class MetricsTests
{
    [Fact]
    public void PrecisionAtK_uses_min_k_eligible_as_denominator()
    {
        // 3 eligible candidates, k=5: denominator is min(5,3)=3.
        var cands = new[]
        {
            new ScoredCandidate("A", 0.9, true),
            new ScoredCandidate("B", 0.8, false),
            new ScoredCandidate("C", 0.7, true),
        };
        var p = Metrics.PrecisionAtK(cands, 5);
        Assert.Equal(2.0 / 3.0, p!.Value, 9);
    }

    [Fact]
    public void Deterministic_tiebreak_is_score_desc_then_bccid()
    {
        var cands = new[]
        {
            new ScoredCandidate("Z", 0.5, false),
            new ScoredCandidate("A", 0.5, true),
        };
        var top1 = Metrics.TopK(cands, 1);
        Assert.Equal("A", top1[0].BccId); // tie on score → BccId ascending
    }

    [Fact]
    public void Zero_positive_fold_has_null_recall_and_is_excluded_from_macro()
    {
        var cands = new[]
        {
            new ScoredCandidate("A", 0.9, false),
            new ScoredCandidate("B", 0.8, false),
        };
        var fold = Metrics.ForFold(7, cands, 5);
        Assert.Null(fold.Recall);                 // N/A, not 0
        Assert.NotNull(fold.Precision);           // precision still defined (= 0)
        Assert.Equal(0.0, fold.Precision!.Value, 9);

        var macro = Metrics.Macro(new double?[] { null, 0.5, null, 1.0 });
        Assert.Equal(0.75, macro!.Value, 9);      // nulls skipped
    }

    [Fact]
    public void Empty_period_returns_null_precision()
        => Assert.Null(Metrics.PrecisionAtK(Array.Empty<ScoredCandidate>(), 5));
}
