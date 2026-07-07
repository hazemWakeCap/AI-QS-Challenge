using System.Text;

namespace QsEarlyWarning.Infrastructure.Import;

/// <summary>
/// Phase-1 thesis proof (plan §5c, Finding 11): the withheld EVM columns are *derivable*. Each field
/// compares the value computed by the <c>cost_centre_evm</c> view against the value recorded in
/// 9_HISTORICAL_DATA, using field-specific tolerances (not one blanket threshold), skipping rows
/// where the recorded value is missing/sentinel or a denominator is zero.
/// </summary>
public sealed class FieldRecon
{
    public required string Field { get; init; }
    public required double RelTol { get; init; }
    public required double AbsTol { get; init; }
    public int Compared { get; set; }
    public int Matched { get; set; }
    public int Mismatched => Compared - Matched;
    public double MatchRate => Compared == 0 ? 1.0 : (double)Matched / Compared;
    public readonly List<string> Worst = new();

    public bool Within(double computed, double recorded)
        => Math.Abs(computed - recorded) <= Math.Max(AbsTol, RelTol * Math.Abs(recorded));
}

public sealed class ReconciliationReport
{
    public required string ProjectSlug { get; init; }
    public int Facts { get; set; }
    public int CostCentres { get; set; }
    public int Periods { get; set; }
    public bool Activated { get; set; }
    public string? FailureReason { get; init; }
    public readonly List<string> PublishViolations = new();
    public readonly List<FieldRecon> Fields = new();

    /// <summary>Alert-label agreement is tracked separately (exact match after normalization).</summary>
    public int AlertCompared { get; set; }
    public int AlertMatched { get; set; }
    public int AlertBoundaryExcused { get; set; }
    public readonly List<string> AlertWorst = new();

    /// <summary>Rows excluded because the workbook's own recorded EV/PV contradict its inputs (source errors, not derivation gaps).</summary>
    public int SourceAnomalies { get; set; }
    public readonly List<string> SourceAnomalyExamples = new();

    /// <summary>Pass iff activation succeeded and every numeric field + alert matched within tolerance.</summary>
    public bool Passed =>
        Activated && FailureReason is null
        && Fields.TrueForAll(f => f.Mismatched == 0)
        && AlertCompared == AlertMatched;

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("════════════════════════════════════════════════════════════════════");
        sb.AppendLine($"  RECONCILIATION REPORT — project '{ProjectSlug}'");
        sb.AppendLine("  computed cost_centre_evm  vs  recorded 9_HISTORICAL_DATA");
        sb.AppendLine("════════════════════════════════════════════════════════════════════");
        sb.AppendLine($"  imported: {CostCentres} cost centres × {Periods} periods → {Facts} facts   activated: {Activated}");
        if (FailureReason is not null)
        {
            sb.AppendLine($"  FAILURE: {FailureReason}");
            foreach (var v in PublishViolations) sb.AppendLine($"    - {v}");
            sb.AppendLine("════════════════════════════════════════════════════════════════════");
            return sb.ToString();
        }
        sb.AppendLine();
        sb.AppendLine($"  {"field",-22}{"compared",10}{"matched",10}{"mismatch",10}{"rate",9}");
        sb.AppendLine($"  {new string('-', 61)}");
        foreach (var f in Fields)
            sb.AppendLine($"  {f.Field,-22}{f.Compared,10}{f.Matched,10}{f.Mismatched,10}{f.MatchRate,8:P1}");
        sb.AppendLine($"  {"alert_level",-22}{AlertCompared,10}{AlertMatched,10}{AlertCompared - AlertMatched,10}{(AlertCompared == 0 ? 1.0 : (double)AlertMatched / AlertCompared),8:P1}");
        if (AlertBoundaryExcused > 0)
            sb.AppendLine($"  (alert: {AlertBoundaryExcused} row(s) at the CPI=0.95 boundary counted as matched — label indeterminate within rounding)");
        sb.AppendLine();

        var anyWorst = false;
        foreach (var f in Fields.Where(f => f.Worst.Count > 0))
        {
            anyWorst = true;
            sb.AppendLine($"  worst {f.Field}:");
            foreach (var w in f.Worst.Take(3)) sb.AppendLine($"    {w}");
        }
        if (AlertWorst.Count > 0)
        {
            anyWorst = true;
            sb.AppendLine("  worst alert_level:");
            foreach (var w in AlertWorst.Take(3)) sb.AppendLine($"    {w}");
        }
        if (!anyWorst) sb.AppendLine("  no mismatches.");

        if (SourceAnomalies > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"  source anomalies excluded: {SourceAnomalies} row(s) where the workbook's own recorded");
            sb.AppendLine("  EV/PV contradict Actual%×BAC / Plan%×BAC (source errors, not derivation gaps):");
            foreach (var w in SourceAnomalyExamples.Take(5)) sb.AppendLine($"    {w}");
        }

        sb.AppendLine("════════════════════════════════════════════════════════════════════");
        sb.AppendLine(Passed
            ? "  VERDICT: PASS — every withheld EVM field is reproduced from inputs within tolerance."
            : "  VERDICT: FAIL — see mismatches above.");
        sb.AppendLine("════════════════════════════════════════════════════════════════════");
        return sb.ToString();
    }
}
