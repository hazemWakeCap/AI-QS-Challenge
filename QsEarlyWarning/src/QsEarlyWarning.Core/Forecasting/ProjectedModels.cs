namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// A cost-centre EVM row at a period the workbook does not reach — the shapes
/// <see cref="EvmProjector"/> emits.
///
/// <b>Why this is allowed to exist at all.</b> <see cref="ProgressForecaster"/> is emphatic that it
/// forecasts progress and not money, and that rule stands. What it correctly forbids is deriving
/// <i>spend</i> from a projected percentage. It does not forbid deriving EV, because EV is not an
/// independent quantity: the schema defines it as
/// <c>ev_amount = round(actual_pct_complete / 100.0 * bac_amount, 2)</c> (0002_schema.sql). Turning a
/// projected percentage into a projected EV is that same arithmetic on a projected input, not a second
/// cost model smuggled in behind one.
///
/// So the composition here is deliberate and narrow:
/// <list type="bullet">
///   <item>EV comes from <see cref="ProgressForecaster"/>, through the schema's own identity.</item>
///   <item>AC comes from <see cref="IncrementalSpendForecaster"/>, which is the sanctioned spend
///         projection, or it comes from nowhere and the row says so.</item>
///   <item>CPI is the ratio of the two — and because they are independent projections, it <i>moves</i>.
///         That movement is the point: a centre whose work is slowing while its spend is not shows up
///         as a sliding CPI several periods before the month-end close would say so.</item>
///   <item>PV and SPI are null past the origin. The baseline curve in <c>9_HISTORICAL_DATA</c> ends at
///         the same period the actuals do, so there is no planned value to compare against and none is
///         invented.</item>
/// </list>
/// </summary>
public enum ProjectionBasis
{
    /// <summary>Reported in the workbook. Not a projection — the row is passed through unchanged.</summary>
    Measured,

    /// <summary>Inside both engines' back-tested horizons: a published error bar stands behind it.</summary>
    Forecast,

    /// <summary>Past at least one engine's back-tested horizon. Same arithmetic, no measured accuracy.</summary>
    Extrapolated,
}

/// <summary>One cost centre's EVM position at one period, measured or projected.</summary>
public sealed record ProjectedCentreRow
{
    public required string BccId { get; init; }
    public required int PeriodId { get; init; }
    public required ProjectionBasis Basis { get; init; }

    public string? Discipline { get; init; }
    public required string PackageCode { get; init; }
    public required string Lifecycle { get; init; }

    /// <summary>Budget at completion — time-invariant, carried from the origin row on projected periods.</summary>
    public required double Bac { get; init; }

    public required double PctComplete { get; init; }
    public double? PctP10 { get; init; }
    public double? PctP90 { get; init; }

    /// <summary>BAC × PctComplete/100 — the schema's definition, applied to a projected percentage.</summary>
    public required double Ev { get; init; }
    public double? EvP10 { get; init; }
    public double? EvP90 { get; init; }

    public double? Ac { get; init; }
    public double? AcP10 { get; init; }
    public double? AcP90 { get; init; }

    /// <summary>False when no spend projection could be formed for this centre. EV still stands;
    /// AC, CPI, EAC and VAC are null rather than guessed.</summary>
    public required bool AcAvailable { get; init; }
    public string? AcNote { get; init; }

    public double? Cv { get; init; }
    public double? Cpi { get; init; }
    public double? Eac { get; init; }
    public double? Vac { get; init; }
    public double? PctBudgetConsumed { get; init; }

    /// <summary>Null past the origin — the baseline curve ends where the actuals do.</summary>
    public double? Pv { get; init; }
    public double? Spi { get; init; }
    public double? PlannedPct { get; init; }

    public required string AlertLevel { get; init; }

    /// <summary>True when the alert was recomputed from a projected CPI rather than reported.</summary>
    public required bool AlertProjected { get; init; }

    public int? ProjectedFinishPeriod { get; init; }
    public double PacePctPerPeriod { get; init; }
    public bool Stalled { get; init; }
}

/// <summary>A whole panel at one period, with the provenance a reader needs to weigh it.</summary>
public sealed record ProjectedPanel
{
    public required int Period { get; init; }
    public required int OriginPeriod { get; init; }
    public required int HorizonPeriod { get; init; }

    /// <summary>Last period whose <i>progress</i> carries a measured error bar.</summary>
    public required int BacktestedThroughPeriod { get; init; }

    /// <summary>Last period whose <i>spend</i> carries a measured error bar. Past it the cost figures
    /// continue on a held run-rate with a widened band and no accuracy claim.</summary>
    public required int SpendBacktestedThroughPeriod { get; init; }

    public required ProjectionBasis Basis { get; init; }
    public required string Method { get; init; }

    public required bool PvAvailable { get; init; }
    public string? PvReason { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
    public required IReadOnlyList<ProjectedCentreRow> Centres { get; init; }
}
