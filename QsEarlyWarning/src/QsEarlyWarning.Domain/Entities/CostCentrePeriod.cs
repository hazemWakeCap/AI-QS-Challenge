namespace QsEarlyWarning.Domain.Entities;

/// <summary>
/// One cost centre (BCC) in one reporting period, from 9_HISTORICAL_DATA.
///
/// Data caveats baked in (see exploration/9-historical-data-columns.md):
///  - The *_Period columns are CUMULATIVE in this workbook (AC_AED_Period == AC_AED_Cumulative);
///    <see cref="AcCumulative"/> maps to AC_AED_Period accordingly.
///  - Percentages are stored as percent (0..100), NOT fractions. So gap is in percentage points.
///
/// Nullable numerics: NOT STARTED / sentinel ("-") cells parse to null (missing), never a parse error.
/// Only rows eligible for pairing/scoring are required to have finite numerics.
/// </summary>
public sealed record CostCentrePeriod
{
    // Identifiers & classification
    public required int PeriodId { get; init; }
    public required string BccId { get; init; }
    public string? MonthYear { get; init; }
    public string? WbsCode { get; init; }
    public string? Discipline { get; init; }
    public required string PackageCode { get; init; }

    /// <summary>Status label: GREEN | AMBER | CLOSED | NOT STARTED | null.</summary>
    public string? AlertLevel { get; init; }

    // Budget
    public double? BacAed { get; init; }

    // Plan
    public double? PlanPctComplete { get; init; }
    public double? PvAed { get; init; }

    // Actuals
    public double? ActualPctComplete { get; init; }
    public double? EarnedQtyCumul { get; init; }
    public double? EvAed { get; init; }

    /// <summary>AC_AED_Cumulative (mapped from AC_AED_Period — cumulative in this workbook).</summary>
    public double? AcCumulative { get; init; }

    // EVM metrics (as recorded)
    public double? CvAed { get; init; }
    public double? Cpi { get; init; }
    public double? Spi { get; init; }
    public double? EacAed { get; init; }
    public double? VacAed { get; init; }
    public double? PctBudgetConsumed { get; init; }

    // Resource split (cumulative basis)
    public double? AcMaterial { get; init; }
    public double? AcManpower { get; init; }
    public double? AcEquipment { get; init; }
    public double? AcSubcontract { get; init; }

    // Signals
    public double? VariancePct { get; init; }
    public double? Rolling3mCpi { get; init; }
    public double? EacVsBacRatio { get; init; }

    /// <summary>Eligible-current: GREEN and scoreable (finite CPI + gap inputs). Plan §6.3.</summary>
    public bool IsScoreableGreen =>
        string.Equals(AlertLevel, "GREEN", StringComparison.OrdinalIgnoreCase)
        && Cpi is double c && double.IsFinite(c)
        && PctBudgetConsumed is double p && double.IsFinite(p)
        && ActualPctComplete is double a && double.IsFinite(a);

    /// <summary>gap = Pct_Budget_Consumed − Actual_Pct_Complete, in percentage points.</summary>
    public double? Gap =>
        PctBudgetConsumed is double p && ActualPctComplete is double a ? p - a : null;
}
