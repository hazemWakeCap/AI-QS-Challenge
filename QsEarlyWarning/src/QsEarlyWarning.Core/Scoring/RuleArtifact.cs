namespace QsEarlyWarning.Core.Scoring;

/// <summary>
/// A frozen, versioned rule scorer fit for one training cutoff. Plan §6.6b.
///
/// trainingCutoffPeriod is an EXCLUSIVE scoring origin: trained on pairs with p &lt; cutoff,
/// scores feature-period == cutoff. Role: OOF (has outcome metrics) or Forecast (cutoff 12, none).
///
/// Only x* and gap_scale are fit from data; the weights and cpi_band are fixed v1 constants.
/// </summary>
public sealed record RuleArtifact
{
    public const string ScorerName = "rule";
    public const string ScorerVersion = "RuleRiskScore@v1";

    public required int TrainingCutoffPeriod { get; init; }
    public required ArtifactRole Role { get; init; }

    /// <summary>Gap zero-point (pp), fit on the training prefix only.</summary>
    public required double XStar { get; init; }

    /// <summary>Gap normalizer (pp) = training-fold IQR of gap (fallback 5pp).</summary>
    public required double GapScale { get; init; }

    /// <summary>Hash of the p &lt; cutoff rows this was fit on (reload validation).</summary>
    public required string TrainingPrefixFingerprint { get; init; }

    // Fixed v1 constants (asserted, never estimated).
    public double WGap => 0.7;
    public double WCpi => 0.3;
    public double CpiBand => 0.10;
}

public enum ArtifactRole
{
    Oof,
    Forecast,
}
