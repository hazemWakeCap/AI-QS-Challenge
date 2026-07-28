using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Features;

namespace QsEarlyWarning.Core.Scoring;

/// <summary>
/// Challengers that ask whether a cost centre's NEIGHBOURS predict its drift.
///
/// <para><b>Two questions, deliberately separated.</b> The obvious feature — "how are the other
/// centres in my zone doing?" — is not the spatial question it looks like. On Tower X, zone and
/// discipline are collinear (see <see cref="ZoneDisciplineCollinearity"/>: no discipline spans more
/// than one zone), so a zone-peer feature measures <b>trade</b>. The genuinely spatial version has
/// to exclude same-trade peers, which is only possible in FLOORS-ALL, the one zone with real trade
/// mixing. Both are reported, labelled for what they actually are.</para>
///
/// <para><b>The blend weight is predeclared, not fitted.</b> Fitting a weight on the same folds
/// that report the result is the selection bias the whole harness exists to avoid — the rule's own
/// x* is fit on the train prefix only, never on the evaluation fold. A challenger with a
/// tuned-in-hindsight weight would beat the rule by construction and prove nothing, so
/// <see cref="PeerWeight"/> is frozen at a plain value and the comparison is honest.</para>
///
/// <para>Both are <b>descriptive only</b>. Nothing here is served: the deployed scorer stays
/// <c>RuleRiskScore@v1</c> unless a challenger beats it out-of-fold on its own merits.</para>
/// </summary>
public static class PeerScorers
{
    /// <summary>
    /// How much the neighbourhood is allowed to move the ranking. Predeclared, never fitted.
    /// Small on purpose: this is a hypothesis being tested, not a rebalancing of the rule.
    /// </summary>
    public const double PeerWeight = 0.15;

    /// <summary>
    /// CPI band over which a neighbourhood's performance is converted to a 0..1 risk contribution,
    /// mirroring the rule's own <c>CpiBand</c> so the two components are on the same scale.
    /// </summary>
    private const double PeerBand = 0.10;

    public static readonly IReadOnlyList<(string Label, Func<TransitionPair, RuleArtifact, double> Score)> All =
        new (string, Func<TransitionPair, RuleArtifact, double>)[]
        {
            // Same zone — which on this project means the same trade. Well-powered: ~98% of the
            // GREEN population has at least one such peer.
            ("peer-trade", (p, a) => Blend(RuleScorer.Score(p, a), p.PeerCpi)),

            // Same zone, DIFFERENT trade. The only genuinely spatial signal available, and only
            // defined inside FLOORS-ALL — everywhere else there are no cross-trade neighbours, so
            // the row falls back to the rule's own score and the comparison stays like-for-like.
            ("peer-spatial", (p, a) => Blend(RuleScorer.Score(p, a), p.PeerCpiCrossTrade)),
        };

    /// <summary>
    /// Adds a neighbourhood risk contribution to the rule's score.
    ///
    /// <para>A missing peer CPI returns the rule's score unchanged rather than contributing zero
    /// risk. The distinction matters: "this centre has no neighbours to judge" must not be scored
    /// as "this centre's neighbourhood is healthy", and it must not silently drop the row from the
    /// population either — every challenger ranks exactly the same centres as the rule.</para>
    /// </summary>
    private static double Blend(double ruleScore, double? peerCpi)
    {
        if (peerCpi is not double cpi || !double.IsFinite(cpi)) return ruleScore;

        // Same shape as RuleScorer.CpiProximity: maximal as the neighbourhood approaches 0.95.
        double peerRisk = RuleScorer.Clamp01(1.0 - (cpi - 0.95) / PeerBand);
        return ruleScore + PeerWeight * peerRisk;
    }

    public static IReadOnlyList<ScoredCandidate> Candidates(
        IEnumerable<TransitionPair> periodPairs,
        Func<TransitionPair, RuleArtifact, double> score,
        RuleArtifact artifact)
        => periodPairs.Select(p => new ScoredCandidate(p.BccId, score(p, artifact), p.Label)).ToList();
}
