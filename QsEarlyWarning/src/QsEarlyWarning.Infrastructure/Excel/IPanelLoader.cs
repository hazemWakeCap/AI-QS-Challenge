using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Infrastructure.Excel;

/// <summary>Loads the raw ordered panel from 9_HISTORICAL_DATA. Plan §6.2.</summary>
public interface IPanelLoader
{
    /// <summary>
    /// The full raw ordered panel (EP- rows only, sorted by BccId, PeriodId), sentinels parsed
    /// as missing. NOT STARTED / zero-earned rows are RETAINED — eligibility is applied later
    /// during pairing/scoring, not at load.
    /// </summary>
    IReadOnlyList<CostCentrePeriod> Load(string workbookPath);
}
