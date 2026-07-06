using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Features;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Scoring;

/// <summary>Result of a scoring request, distinguishing not-found from malformed. Plan §6.9.</summary>
public enum ScoreStatus { Ok, NoArtifact }

public sealed record ScoreResult
{
    public required ScoreStatus Status { get; init; }
    public required bool IsForecast { get; init; }
    public required string? ArtifactVersion { get; init; }
    public required int TrainingCutoffPeriod { get; init; }
    public required IReadOnlyList<WatchlistRow> Rows { get; init; }

    public static ScoreResult NotFound() => new()
    {
        Status = ScoreStatus.NoArtifact, IsForecast = false, ArtifactVersion = null,
        TrainingCutoffPeriod = 0, Rows = Array.Empty<WatchlistRow>(),
    };
}

/// <summary>
/// The single shared scoring path (plan §6.7). Resolves artifactFor(period) and ranks the
/// GREEN-at-p centres. Never trains at request time; a well-formed period with no matching
/// artifact returns NoArtifact (→ 404), never a future-trained fallback.
/// </summary>
public sealed class WatchlistScoringService
{
    private readonly FeatureBuilder _features = new();

    public ScoreResult ScorePeriod(IReadOnlyList<CostCentrePeriod> panel, int periodId, TrainedModel model)
    {
        var artifact = model.ArtifactFor(periodId);
        if (artifact is null)
            return ScoreResult.NotFound();

        // Scoring needs no successor/label — use the successor-free builder so the forecast
        // period (12, no successor) still produces a watchlist.
        var pairs = _features.BuildScoringRows(panel, periodId);

        var rows = pairs
            .Select(p => new WatchlistRow
            {
                BccId = p.BccId,
                Discipline = p.Discipline,
                PackageCode = p.PackageCode,
                PeriodId = p.PeriodId,
                RiskScore = RuleScorer.Score(p, artifact),
                Cpi = p.Cpi,
                Gap = p.Gap,
                RiskIndicators = RuleScorer.RiskIndicators(p, artifact),
            })
            .OrderByDescending(r => r.RiskScore)
            .ThenBy(r => r.BccId, StringComparer.Ordinal)
            .ToList();

        return new ScoreResult
        {
            Status = ScoreStatus.Ok,
            IsForecast = artifact.Role == ArtifactRole.Forecast,
            ArtifactVersion = RuleArtifact.ScorerVersion,
            TrainingCutoffPeriod = artifact.TrainingCutoffPeriod,
            Rows = rows,
        };
    }
}
