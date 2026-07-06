using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Features;

namespace QsEarlyWarning.Core.Scoring;

/// <summary>
/// CPI-native ranking comparators — the descriptive baselines the deployed rule is reported
/// against. Plan §6.4. Three DISTINCT comparators (ranking by Cpi and by −distTo095 is the
/// same ordering, so only Cpi is kept).
/// </summary>
public static class CpiNativeScorers
{
    public static readonly IReadOnlyList<(string Label, Func<TransitionPair, double> Score)> All =
        new (string, Func<TransitionPair, double>)[]
        {
            // Lower CPI = riskier → higher score.
            ("cpi", p => -p.Cpi),
            // More negative 1-period CPI change = riskier. Missing → no signal (0).
            ("dCpi1", p => -(p.DCpi1 ?? 0.0)),
            // Lower rolling-3M CPI = riskier. Missing → fall back to spot CPI.
            ("rolling3mCpi", p => -(p.Rolling3mCpi ?? p.Cpi)),
        };

    public static IReadOnlyList<ScoredCandidate> Candidates(
        IEnumerable<TransitionPair> periodPairs, Func<TransitionPair, double> score)
        => periodPairs.Select(p => new ScoredCandidate(p.BccId, score(p), p.Label)).ToList();
}
