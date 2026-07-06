namespace QsEarlyWarning.Domain.Constants;

/// <summary>
/// Frozen constants that bound the experiment. See plan §6.1.
/// No ChallengerMarginPp — the rule is the predeclared deployed scorer and there is
/// no adaptive gate, so a decision-margin constant has no defined dataset.
/// </summary>
public static class EvmThresholds
{
    /// <summary>AMBER business threshold: AMBER ≡ CPI &lt; 0.95 on live GREEN/AMBER rows.</summary>
    public const double CpiThreshold = 0.95;

    /// <summary>Reported ranking depths.</summary>
    public static readonly int[] TopK = { 5, 10 };

    /// <summary>The single k that fits x* (k=10 is reported but never selects).</summary>
    public const int SelectionK = 5;

    /// <summary>First rolling origin scored (needs history before it).</summary>
    public const int MinTrainOrigin = 4;

    /// <summary>Last period with a known successor (12 has no successor yet).</summary>
    public const int LastLabeledPeriod = 11;

    /// <summary>Latest observed feature period → the live forecast cutoff.</summary>
    public const int ForecastPeriod = 12;

    public const int RandomSeed = 0;
}
