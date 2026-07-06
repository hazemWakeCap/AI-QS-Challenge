namespace QsEarlyWarning.Core.Scoring;

/// <summary>One ranked GREEN centre in a watchlist. Plan §6.7.</summary>
public sealed record WatchlistRow
{
    public required string BccId { get; init; }
    public required string? Discipline { get; init; }
    public required string PackageCode { get; init; }
    public required int PeriodId { get; init; }
    public required double RiskScore { get; init; }
    public required double Cpi { get; init; }
    public required double Gap { get; init; }

    /// <summary>2–3 deterministic reason codes (contextual observations, not causal attributions).</summary>
    public required IReadOnlyList<string> RiskIndicators { get; init; }
}
