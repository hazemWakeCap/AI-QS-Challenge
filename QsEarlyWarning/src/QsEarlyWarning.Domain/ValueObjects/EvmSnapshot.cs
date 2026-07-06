using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Domain.ValueObjects;

/// <summary>
/// EVM identities derived from a cost-centre row. Plan §6.1.
///
/// Distinguishes true identities (always hold) from the CPI forecasting formula:
///   Identities: CV = EV − AC, CPI = EV / AC, SPI = EV / PV, VAC = BAC − EAC
///   EAC is NOT an identity: EAC = BAC / CPI is only one forecasting assumption.
///     - <see cref="EacRecorded"/> is the workbook's raw EAC_AED (source of truth).
///     - <see cref="EacCpiMethod"/> is the CPI-derived estimate, exposed separately.
/// Never used to fabricate the withheld budget/EV sheets — reads recorded values only.
/// </summary>
public sealed record EvmSnapshot
{
    public required string BccId { get; init; }
    public required int PeriodId { get; init; }

    public double? Bac { get; init; }
    public double? Pv { get; init; }
    public double? Ev { get; init; }
    public double? Ac { get; init; }
    public double? EacRecorded { get; init; }

    /// <summary>CV = EV − AC (identity).</summary>
    public double? Cv => Ev is double e && Ac is double a ? e - a : null;

    /// <summary>CPI = EV / AC (identity; guarded).</summary>
    public double? Cpi => Ev is double e && Ac is double a && a != 0 ? e / a : null;

    /// <summary>SPI = EV / PV (identity; guarded).</summary>
    public double? Spi => Ev is double e && Pv is double p && p != 0 ? e / p : null;

    /// <summary>CPI-method forecast: EAC = BAC / CPI. One assumption, not an identity.</summary>
    public double? EacCpiMethod => Bac is double b && Cpi is double c && c != 0 ? b / c : null;

    /// <summary>VAC = BAC − EAC (uses recorded EAC when present, else CPI-method).</summary>
    public double? Vac
    {
        get
        {
            var eac = EacRecorded ?? EacCpiMethod;
            return Bac is double b && eac is double e ? b - e : null;
        }
    }

    public static EvmSnapshot From(CostCentrePeriod r) => new()
    {
        BccId = r.BccId,
        PeriodId = r.PeriodId,
        Bac = r.BacAed,
        Pv = r.PvAed,
        Ev = r.EvAed,
        Ac = r.AcCumulative,
        EacRecorded = r.EacAed,
    };
}
