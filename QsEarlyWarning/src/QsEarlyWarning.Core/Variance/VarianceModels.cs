namespace QsEarlyWarning.Core.Variance;

// Idea-5 Variance Attribution Bridge. Two honest lanes over the EVM identities, never folded together:
//  - Cost/efficiency lane (CV = EV − AC), decomposed by resource via estimate shares (attribution, not
//    cause: CV has NO quantity term and cannot be split price-vs-productivity on this data).
//  - Schedule/progress lane (SV = EV − PV), monetary only.
// Every computed money field is nullable so a missing / non-EP / non-live / null-money row is represented
// as Available=false (never zero-coerced). All amounts AED.

/// <summary>One resource's contribution to the cost variance. <c>EvR = EV × NormShare</c> (norm-implied
/// earned budget for the resource), <c>AcR</c> = recorded actual split, <c>CvR = EvR − AcR</c>,
/// <c>TimesNormBudget = AcR / EvR</c> ("ran ~1.8× its norm-implied budget").</summary>
public sealed record ResourceContribution(
    string ResourceType, double NormShare, double EvR, double AcR, double CvR, double? TimesNormBudget);

/// <summary>The two-lane variance bridge for one (BccId, PeriodId). Tie-out anchor:
/// <c>Σ CvR + UnexplainedResidual == CvAed</c> (UnexplainedResidual = ΣAcR − Ac) and <c>SvAed == Ev − Pv</c>.</summary>
public sealed record VarianceBridge(
    string BccId, int PeriodId, bool Available, string? UnavailableReason,
    string? Package, string? Discipline,
    double? Bac, double? Pv, double? Ev, double? Ac, double? CvAed, double? SvAed, double? Spi,
    IReadOnlyList<ResourceContribution> Contributions, string? DominantResourceType,
    double? UnexplainedResidual, bool TiesOut, bool ResourceBreakdownAvailable,
    bool AssumptionBased, string? EvidenceNeeded, IReadOnlyList<string> Notes)
{
    public static VarianceBridge Unavailable(string bccId, int periodId, string reason) =>
        new(bccId, periodId, Available: false, UnavailableReason: reason, Package: null, Discipline: null,
            Bac: null, Pv: null, Ev: null, Ac: null, CvAed: null, SvAed: null, Spi: null,
            Contributions: Array.Empty<ResourceContribution>(), DominantResourceType: null,
            UnexplainedResidual: null, TiesOut: false, ResourceBreakdownAvailable: false,
            AssumptionBased: false, EvidenceNeeded: null, Notes: new[] { reason });
}
