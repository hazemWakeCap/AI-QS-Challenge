using System.Security.Cryptography;
using System.Text;
using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Features;
using QsEarlyWarning.Domain.Constants;

namespace QsEarlyWarning.Core.Scoring;

/// <summary>
/// Fits a <see cref="RuleArtifact"/> from a training prefix. Plan §6.4 / §6.6b.
///
/// Only x* and gap_scale are fit — both on training pairs only (never the reporting fold):
///   - gap_scale = IQR of gap over training pairs (fallback 5pp if degenerate).
///   - x* sweeps the declared grid {0.0, 0.5, …, 20.0} pp and maximizes the macro-mean
///     (across training cycles) of precision@SelectionK; tie-break = smallest x*.
/// </summary>
public sealed class RuleFitter
{
    private const double GapScaleFallback = 5.0; // pp
    private static readonly double[] XStarGrid = BuildGrid();

    private static double[] BuildGrid()
    {
        var g = new List<double>();
        for (double x = 0.0; x <= 20.0 + 1e-9; x += 0.5) g.Add(Math.Round(x, 2));
        return g.ToArray();
    }

    /// <summary>
    /// Fits an artifact. trainPairs must be the p &lt; cutoff prefix ONLY.
    /// </summary>
    public RuleArtifact Fit(IReadOnlyList<TransitionPair> trainPairs, int trainingCutoffPeriod, ArtifactRole role)
    {
        double gapScale = ComputeGapScale(trainPairs);

        double bestX = XStarGrid[0];
        double bestObjective = double.NegativeInfinity;

        foreach (var x in XStarGrid)
        {
            var probe = new RuleArtifact
            {
                TrainingCutoffPeriod = trainingCutoffPeriod,
                Role = role,
                XStar = x,
                GapScale = gapScale,
                TrainingPrefixFingerprint = "probe",
            };

            double? obj = MacroPrecisionAtSelectionK(trainPairs, probe);
            if (obj is double o && o > bestObjective + 1e-12)
            {
                bestObjective = o;
                bestX = x; // grid ascending → first max wins → smallest-x tie-break
            }
        }

        return new RuleArtifact
        {
            TrainingCutoffPeriod = trainingCutoffPeriod,
            Role = role,
            XStar = bestX,
            GapScale = gapScale,
            TrainingPrefixFingerprint = Fingerprint(trainPairs),
        };
    }

    private static double ComputeGapScale(IReadOnlyList<TransitionPair> pairs)
    {
        if (pairs.Count < 4) return GapScaleFallback;
        var gaps = pairs.Select(p => p.Gap).OrderBy(g => g).ToList();
        double q1 = Quantile(gaps, 0.25);
        double q3 = Quantile(gaps, 0.75);
        double iqr = q3 - q1;
        return iqr > 1e-6 ? iqr : GapScaleFallback;
    }

    private static double Quantile(IReadOnlyList<double> sorted, double q)
    {
        if (sorted.Count == 1) return sorted[0];
        double pos = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        double frac = pos - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }

    /// <summary>Macro-mean over training cycles (feature periods) of precision@SelectionK.</summary>
    private static double? MacroPrecisionAtSelectionK(IReadOnlyList<TransitionPair> pairs, RuleArtifact probe)
    {
        var perCycle = pairs
            .GroupBy(p => p.PeriodId)
            .Select(g =>
            {
                var scored = g.Select(p => new ScoredCandidate(p.BccId, RuleScorer.Score(p, probe), p.Label)).ToList();
                return Metrics.PrecisionAtK(scored, EvmThresholds.SelectionK);
            });
        return Metrics.Macro(perCycle);
    }

    private static string Fingerprint(IReadOnlyList<TransitionPair> pairs)
    {
        var sb = new StringBuilder();
        foreach (var p in pairs.OrderBy(p => p.BccId, StringComparer.Ordinal).ThenBy(p => p.PeriodId))
            sb.Append(p.BccId).Append('|').Append(p.PeriodId).Append('|')
              .Append(p.Cpi.ToString("R")).Append('|').Append(p.Gap.ToString("R")).Append('|')
              .Append(p.Label ? '1' : '0').Append(';');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16];
    }
}
