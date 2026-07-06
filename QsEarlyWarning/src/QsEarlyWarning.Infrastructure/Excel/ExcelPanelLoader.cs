using System.Globalization;
using ClosedXML.Excel;
using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Infrastructure.Excel;

/// <summary>
/// Reads 9_HISTORICAL_DATA via ClosedXML. Plan §6.2.
///
/// Header is row 5 (1-indexed); data starts row 6. Keeps only Package_Code starting "EP-"
/// (drops the junk AC_Cumul block). Sentinels ("-", blank) parse as missing. The raw ordered
/// panel is returned intact — NOT STARTED / zero-earned rows are kept for lag/adjacency and
/// excluded only later during pairing/scoring.
///
/// Two validation layers (kept distinct):
///   - Production schema/semantic validation lives HERE (fail loud): sheet/header present,
///     (BccId,PeriodId) unique, BccId non-blank, PeriodId in range, AlertLevel in permitted set,
///     finite numerics on eligible rows.
///   - The exact "173 BccId / 0 CPI-label mismatches" snapshot checks live in tests, not here.
/// </summary>
public sealed class ExcelPanelLoader : IPanelLoader
{
    private const string SheetName = "9_HISTORICAL_DATA";
    private const int HeaderRow = 5;
    private const int MinPeriod = 1;
    private const int MaxPeriod = 12;

    private static readonly HashSet<string> PermittedAlerts = new(StringComparer.OrdinalIgnoreCase)
    {
        "GREEN", "AMBER", "CLOSED", "NOT STARTED",
    };

    public IReadOnlyList<CostCentrePeriod> Load(string workbookPath)
    {
        if (!File.Exists(workbookPath))
            throw new DataContractException($"Workbook not found: {workbookPath}");

        using var wb = new XLWorkbook(workbookPath);
        if (!wb.TryGetWorksheet(SheetName, out var ws))
            throw new DataContractException($"Sheet '{SheetName}' not found.");

        var col = MapHeader(ws);

        var rows = new List<CostCentrePeriod>();
        var seenKeys = new HashSet<(string, int)>();
        var lastDataRow = ws.LastRowUsed()?.RowNumber() ?? HeaderRow;

        for (int r = HeaderRow + 1; r <= lastDataRow; r++)
        {
            var pkg = Str(ws, r, col, "Package_Code");
            if (pkg is null || !pkg.StartsWith("EP-", StringComparison.OrdinalIgnoreCase))
                continue; // drops the junk AC_Cumul block and blank/numeric-package rows

            var bcc = Str(ws, r, col, "BCC_ID");
            if (string.IsNullOrWhiteSpace(bcc))
                throw new DataContractException($"Blank BCC_ID at row {r}.");

            var period = Int(ws, r, col, "Period_ID");
            if (period is null || period < MinPeriod || period > MaxPeriod)
                throw new DataContractException($"Period_ID out of range [{MinPeriod},{MaxPeriod}] at row {r}: {period}.");

            if (!seenKeys.Add((bcc!, period.Value)))
                throw new DataContractException($"Duplicate (BccId, PeriodId) = ({bcc}, {period}) at row {r}.");

            var alert = Str(ws, r, col, "Alert_Level");
            if (alert is not null && !PermittedAlerts.Contains(alert))
                throw new DataContractException($"Unexpected Alert_Level '{alert}' at row {r}.");

            var row = new CostCentrePeriod
            {
                PeriodId = period.Value,
                BccId = bcc!,
                MonthYear = Str(ws, r, col, "Month_Year"),
                WbsCode = Str(ws, r, col, "WBS_Code"),
                Discipline = Str(ws, r, col, "Discipline"),
                PackageCode = pkg,
                AlertLevel = alert,
                BacAed = Num(ws, r, col, "BAC_AED"),
                PlanPctComplete = Num(ws, r, col, "Plan_Pct_Complete"),
                PvAed = Num(ws, r, col, "PV_AED"),
                ActualPctComplete = Num(ws, r, col, "Actual_Pct_Complete"),
                EarnedQtyCumul = Num(ws, r, col, "Earned_Qty_Cumul"),
                EvAed = Num(ws, r, col, "EV_AED"),
                AcCumulative = Num(ws, r, col, "AC_AED_Period"), // cumulative in this workbook
                CvAed = Num(ws, r, col, "CV_AED"),
                Cpi = Num(ws, r, col, "CPI"),
                Spi = Num(ws, r, col, "SPI"),
                EacAed = Num(ws, r, col, "EAC_AED"),
                VacAed = Num(ws, r, col, "VAC_AED"),
                PctBudgetConsumed = Num(ws, r, col, "Pct_Budget_Consumed"),
                AcMaterial = Num(ws, r, col, "AC_Material_AED"),
                AcManpower = Num(ws, r, col, "AC_Manpower_AED"),
                AcEquipment = Num(ws, r, col, "AC_Equipment_AED"),
                AcSubcontract = Num(ws, r, col, "AC_Subcontract_AED"),
                VariancePct = Num(ws, r, col, "Variance_Pct"),
                Rolling3mCpi = Num(ws, r, col, "Rolling_3M_CPI"),
                EacVsBacRatio = Num(ws, r, col, "EAC_vs_BAC_Ratio"),
            };

            // Finite-numeric requirement applies ONLY to pairing/scoring-eligible rows.
            if (row.IsScoreableGreen)
            {
                foreach (var (name, val) in new[]
                         {
                             ("CPI", row.Cpi), ("Pct_Budget_Consumed", row.PctBudgetConsumed),
                             ("Actual_Pct_Complete", row.ActualPctComplete),
                         })
                {
                    if (val is null || !double.IsFinite(val.Value))
                        throw new DataContractException(
                            $"Non-finite {name} on eligible GREEN row {r} (BccId {bcc}, Period {period}).");
                }
            }

            rows.Add(row);
        }

        if (rows.Count == 0)
            throw new DataContractException("No EP- rows loaded — check the sheet/filter.");

        return rows
            .OrderBy(x => x.BccId, StringComparer.Ordinal)
            .ThenBy(x => x.PeriodId)
            .ToList();
    }

    private static Dictionary<string, int> MapHeader(IXLWorksheet ws)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headerRow = ws.Row(HeaderRow);
        var lastCol = headerRow.LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (int c = 1; c <= lastCol; c++)
        {
            var name = headerRow.Cell(c).GetString().Trim();
            if (!string.IsNullOrEmpty(name) && !map.ContainsKey(name))
                map[name] = c;
        }

        foreach (var required in new[] { "Period_ID", "BCC_ID", "Package_Code", "Alert_Level", "CPI" })
            if (!map.ContainsKey(required))
                throw new DataContractException($"Required column '{required}' missing from header row {HeaderRow}.");

        return map;
    }

    private static string? Str(IXLWorksheet ws, int r, Dictionary<string, int> col, string name)
    {
        if (!col.TryGetValue(name, out var c)) return null;
        var s = ws.Cell(r, c).GetString().Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static int? Int(IXLWorksheet ws, int r, Dictionary<string, int> col, string name)
    {
        var d = Num(ws, r, col, name);
        return d is null ? null : (int)Math.Round(d.Value);
    }

    /// <summary>
    /// Parses a numeric cell. Sentinels ("-", "", "N/A") → null (missing), NOT an error.
    /// Excel error cells / non-numeric text on a genuinely numeric field also → null here;
    /// the eligible-row finite check (above) turns a missing value on a scoreable GREEN row
    /// into a loud failure.
    /// </summary>
    private static double? Num(IXLWorksheet ws, int r, Dictionary<string, int> col, string name)
    {
        if (!col.TryGetValue(name, out var c)) return null;
        var cell = ws.Cell(r, c);
        if (cell.IsEmpty()) return null;

        if (cell.DataType == XLDataType.Number)
        {
            var v = cell.GetDouble();
            return double.IsFinite(v) ? v : null;
        }

        var s = cell.GetString().Trim();
        if (string.IsNullOrEmpty(s) || s is "-" or "—" or "N/A" or "NA") return null;

        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
               && double.IsFinite(parsed)
            ? parsed
            : null;
    }
}
