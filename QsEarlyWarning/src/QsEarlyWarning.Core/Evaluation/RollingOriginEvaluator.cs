using QsEarlyWarning.Core.Features;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Evaluation;

/// <summary>The complete trained model: cutoff-keyed artifacts + the frozen validation summary.</summary>
public sealed record TrainedModel
{
    /// <summary>Rule artifacts keyed by exclusive training cutoff. OOF: [MinTrainOrigin..11]; Forecast: 12.</summary>
    public required IReadOnlyDictionary<int, RuleArtifact> Artifacts { get; init; }
    public required ValidationSummary Summary { get; init; }

    public RuleArtifact? ArtifactFor(int periodId)
        => Artifacts.TryGetValue(periodId, out var a) ? a : null;
}

/// <summary>
/// Walks the rolling origin, fits per-origin OOF rule artifacts (leakage-safe), builds the
/// separate cutoff-12 forecast artifact, and produces the model-level validation summary.
/// Plan §6.6 / §6.6b. No adaptive selection: the rule is the deployed scorer; the challenger,
/// if present, is reported side by side descriptively and never adopted.
/// </summary>
public sealed class RollingOriginEvaluator
{
    public const string FeatureSchemaVersion = "features@v1";

    private readonly FeatureBuilder _features = new();
    private readonly RuleFitter _fitter = new();

    public TrainedModel Train(IReadOnlyList<CostCentrePeriod> panel)
    {
        // All labeled pairs (feature periods 1..LastLabeledPeriod).
        var allPairs = _features.BuildPairs(panel, 1, EvmThresholds.LastLabeledPeriod).Pairs;
        var byPeriod = allPairs.GroupBy(p => p.PeriodId).ToDictionary(g => g.Key, g => g.ToList());

        var artifacts = new Dictionary<int, RuleArtifact>();
        var ruleFolds = new Dictionary<int, List<FoldMetrics>>();   // k -> folds
        var cpiFolds = new Dictionary<string, Dictionary<int, List<FoldMetrics>>>(); // comparator -> k -> folds
        foreach (var k in EvmThresholds.TopK) ruleFolds[k] = new List<FoldMetrics>();
        foreach (var (label, _) in CpiNativeScorers.All)
            cpiFolds[label] = EvmThresholds.TopK.ToDictionary(k => k, _ => new List<FoldMetrics>());

        // OOF artifacts + folds for origins [MinTrainOrigin .. LastLabeledPeriod].
        for (int o = EvmThresholds.MinTrainOrigin; o <= EvmThresholds.LastLabeledPeriod; o++)
        {
            var trainPrefix = allPairs.Where(p => p.PeriodId < o).ToList();
            if (trainPrefix.Count == 0 || !byPeriod.TryGetValue(o, out var testFold)) continue;

            var artifact = _fitter.Fit(trainPrefix, trainingCutoffPeriod: o, ArtifactRole.Oof);
            artifacts[o] = artifact;

            foreach (var k in EvmThresholds.TopK)
            {
                var ruleCandidates = testFold
                    .Select(p => new ScoredCandidate(p.BccId, RuleScorer.Score(p, artifact), p.Label))
                    .ToList();
                ruleFolds[k].Add(Metrics.ForFold(o, ruleCandidates, k));

                foreach (var (label, score) in CpiNativeScorers.All)
                    cpiFolds[label][k].Add(Metrics.ForFold(o, CpiNativeScorers.Candidates(testFold, score), k));
            }
        }

        // Forecast artifact: trained on ALL labeled pairs (p < 12), scores period 12. No metrics.
        var forecast = _fitter.Fit(allPairs, EvmThresholds.ForecastPeriod, ArtifactRole.Forecast);
        artifacts[EvmThresholds.ForecastPeriod] = forecast;

        var ruleReports = EvmThresholds.TopK
            .Select(k => new ScorerReport { ScorerLabel = "rule", K = k, Folds = ruleFolds[k] })
            .ToList();

        var cpiReports = CpiNativeScorers.All
            .SelectMany(c => EvmThresholds.TopK.Select(k =>
                new ScorerReport { ScorerLabel = $"cpi-native:{c.Label}", K = k, Folds = cpiFolds[c.Label][k] }))
            .ToList();

        var origins = artifacts.Keys.Where(c => c <= EvmThresholds.LastLabeledPeriod).ToList();
        var summary = new ValidationSummary
        {
            Scorer = RuleArtifact.ScorerName,
            ScorerVersion = RuleArtifact.ScorerVersion,
            FeatureSchemaVersion = FeatureSchemaVersion,
            EvaluationOriginMin = origins.Count == 0 ? 0 : origins.Min(),
            EvaluationOriginMax = origins.Count == 0 ? 0 : origins.Max(),
            FoldCount = ruleFolds[EvmThresholds.SelectionK].Count,
            TotalTransitions = allPairs.Count(p => p.Label),
            Rule = ruleReports,
            CpiNative = cpiReports,
        };

        return new TrainedModel { Artifacts = artifacts, Summary = summary };
    }
}
