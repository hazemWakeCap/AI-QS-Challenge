namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// Regression + interval metrics for the incremental-spend back-test (the ranking-only
/// <c>Metrics.cs</c> has none). Aggregation is defined precisely and both are reported:
/// <see cref="MaePctOfBac"/> = mean-over-centres of the centre's mean `|error|/BacAed`;
/// <see cref="Wape"/> = global `Σ|error| / Σ|actual|`.
/// </summary>
public static class ForecastMetrics
{
    public sealed record Row(string BccId, double Bac, double Actual, double Pred, double? Lo, double? Hi);

    /// <summary>Mean over centres of the centre's mean(|error|/BAC) — centres weighted equally.</summary>
    public static double MaePctOfBac(IReadOnlyList<Row> rows)
    {
        var perCentre = rows.GroupBy(r => r.BccId)
            .Select(g => g.Average(r => r.Bac > 0 ? Math.Abs(r.Actual - r.Pred) / r.Bac : 0.0))
            .ToList();
        return perCentre.Count == 0 ? 0.0 : perCentre.Average() * 100.0;   // percent
    }

    /// <summary>Global weighted absolute percentage error Σ|error| / Σ|actual|.</summary>
    public static double Wape(IReadOnlyList<Row> rows)
    {
        double num = rows.Sum(r => Math.Abs(r.Actual - r.Pred));
        double den = rows.Sum(r => Math.Abs(r.Actual));
        return den > 1e-9 ? num / den * 100.0 : 0.0;
    }

    /// <summary>Fraction of actuals inside [Lo,Hi] (null endpoint = unbounded) among rows with a defined interval.</summary>
    public static (double? Coverage, int N) Coverage(IReadOnlyList<Row> rows)
    {
        var withInterval = rows.Where(r => r.Lo.HasValue || r.Hi.HasValue).ToList();
        if (withInterval.Count == 0) return (null, 0);
        int inside = withInterval.Count(r =>
            (!r.Lo.HasValue || r.Actual >= r.Lo.Value) && (!r.Hi.HasValue || r.Actual <= r.Hi.Value));
        return ((double)inside / withInterval.Count, withInterval.Count);
    }

    /// <summary>Wilson score interval for a binomial proportion (95%).</summary>
    public static (double Low, double High) Wilson(double p, int n, double z = 1.96)
    {
        if (n == 0) return (0, 1);
        double z2 = z * z, denom = 1 + z2 / n;
        double centre = (p + z2 / (2 * n)) / denom;
        double half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4.0 * n * n)) / denom;
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }
}
