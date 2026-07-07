using System.Globalization;
using ClosedXML.Excel;
using QsEarlyWarning.Domain.Estimate;

namespace QsEarlyWarning.Infrastructure.Excel;

/// <summary>
/// Idea-3 estimate reader: loads the joined estimate graph from workbook sheets 1_BOQ,
/// 2_ESTIMATE_NORMS, 3_BOQ_MAPPING, 4_ESTIMATE_DATASHEET via ClosedXML, reusing the
/// <see cref="ExcelPanelLoader"/> sentinel-parsing pattern. Header row is 4 for sheets 2/3/4; sheet 1
/// uses a two-tier header (group labels row 4, real sub-headers row 5) so it is read from row 5.
/// Headers are matched case-insensitively with newlines/whitespace normalized (the workbook headers
/// contain embedded '\n', e.g. "Qty/\nUnit Work").
///
/// Bound to one owning project id (resolved once at startup): <see cref="TryLoadForProject"/> returns
/// the memoized model only for that id; any other id — or a missing/invalid workbook — returns null
/// (fail closed). The workbook is a static file, so it is parsed at most once.
/// </summary>
public sealed class EstimateWorkbookLoader : IEstimateSource
{
    private readonly string _workbookPath;
    private readonly long? _owningProjectId;
    private readonly object _gate = new();
    private EstimateModel? _cached;
    private bool _loaded;

    /// <param name="workbookPath">Path to Tower_X_Project_Data.xlsx.</param>
    /// <param name="owningProjectId">The project the workbook belongs to (resolved from the estimate
    /// slug at startup); null disables the stress test entirely (fail closed).</param>
    public EstimateWorkbookLoader(string workbookPath, long? owningProjectId)
    {
        _workbookPath = workbookPath;
        _owningProjectId = owningProjectId;
    }

    public EstimateModel? TryLoadForProject(long projectId)
    {
        if (_owningProjectId is null || projectId != _owningProjectId.Value) return null;

        lock (_gate)
        {
            if (_loaded) return _cached;
            _loaded = true;
            try { _cached = Load(_workbookPath); }
            catch { _cached = null; } // stress test unavailable; never sink the snapshot
            return _cached;
        }
    }

    private static EstimateModel Load(string path)
    {
        using var wb = new XLWorkbook(path);

        var norms = ReadNorms(wb);
        var mappings = ReadMappings(wb);
        var lines = ReadResourceLines(wb);
        var boq = ReadBoqLines(wb);
        return new EstimateModel(norms, boq, mappings, lines);
    }

    // ── sheet readers ──

    private static List<EstimateNorm> ReadNorms(XLWorkbook wb)
    {
        var (ws, col) = Sheet(wb, "2_ESTIMATE_NORMS", 4);
        var rows = new List<EstimateNorm>();
        for (int r = 5; r <= LastRow(ws); r++)
        {
            var code = Str(ws, r, col, "Norm Code");
            if (code is null) continue;
            rows.Add(new EstimateNorm(
                code,
                Str(ws, r, col, "Disc Code"), Str(ws, r, col, "Discipline Name"),
                Str(ws, r, col, "Sub-Trade Code"), Str(ws, r, col, "Sub-Trade Name"),
                Str(ws, r, col, "Unit"), Num(ws, r, col, "Output Norm"),
                Str(ws, r, col, "Procurement Route"), Str(ws, r, col, "Gang Composition"),
                Num(ws, r, col, "Gang Size"),
                Num(ws, r, col, "Mat1 Qty/UoW"), Num(ws, r, col, "Mat2 Qty/UoW"),
                Str(ws, r, col, "Notes")));
        }
        return rows;
    }

    private static List<BoqMapping> ReadMappings(XLWorkbook wb)
    {
        var (ws, col) = Sheet(wb, "3_BOQ_MAPPING", 4);
        var rows = new List<BoqMapping>();
        for (int r = 5; r <= LastRow(ws); r++)
        {
            var item = Str(ws, r, col, "Item");
            if (item is null) continue;
            rows.Add(new BoqMapping(
                Str(ws, r, col, "BOQ Sec") ?? "", item, Str(ws, r, col, "Unit"),
                Str(ws, r, col, "Norm Code"), Str(ws, r, col, "Estimate Package"),
                Str(ws, r, col, "Op Code"), Str(ws, r, col, "Primary Resource Types"),
                Str(ws, r, col, "Procurement")));
        }
        return rows;
    }

    private static List<ResourceLine> ReadResourceLines(XLWorkbook wb)
    {
        var (ws, col) = Sheet(wb, "4_ESTIMATE_DATASHEET", 4);
        var rows = new List<ResourceLine>();
        for (int r = 5; r <= LastRow(ws); r++)
        {
            var item = Str(ws, r, col, "Item");
            var rtype = Str(ws, r, col, "Resource Type");
            if (item is null || rtype is null) continue;
            rows.Add(new ResourceLine(
                Str(ws, r, col, "BOQ Sec") ?? "", item, Str(ws, r, col, "Norm Code"),
                Str(ws, r, col, "Package"), Str(ws, r, col, "Op Code"), rtype,
                Str(ws, r, col, "Resource Description"), Str(ws, r, col, "Unit"),
                Num(ws, r, col, "BOQ Qty"), Num(ws, r, col, "Qty/Unit Work"),
                Str(ws, r, col, "Consumption Unit"), Num(ws, r, col, "Total Resource Qty"),
                Num(ws, r, col, "Unit Rate"), Num(ws, r, col, "Resource Cost"),
                Num(ws, r, col, "Indirect Cost"), Num(ws, r, col, "Total Contract Amt"),
                Num(ws, r, col, "Gang Output"), Num(ws, r, col, "Gang Size")));
        }
        return rows;
    }

    private static List<BoqLine> ReadBoqLines(XLWorkbook wb)
    {
        // Sheet 1 has a two-tier header: group labels row 4, real sub-headers row 5, data from row 6.
        var (ws, col) = Sheet(wb, "1_BOQ", 5);
        var rows = new List<BoqLine>();
        for (int r = 6; r <= LastRow(ws); r++)
        {
            var item = Str(ws, r, col, "Item Ref");
            if (item is null) continue;
            rows.Add(new BoqLine(
                Str(ws, r, col, "Sec") ?? "", item, Str(ws, r, col, "Description"),
                Str(ws, r, col, "Unit"), Num(ws, r, col, "Quantity"),
                Num(ws, r, col, "Direct+Indirect Amount"), Num(ws, r, col, "Margin %"),
                Num(ws, r, col, "Margin Amount"), Num(ws, r, col, "Cont %"),
                Num(ws, r, col, "Contingency Amount"), Num(ws, r, col, "TOTAL Amount"),
                Str(ws, r, col, "Norm Ref")));
        }
        return rows;
    }

    // ── header mapping + cell parsing (normalize embedded newlines/whitespace) ──

    private static (IXLWorksheet Ws, Dictionary<string, int> Col) Sheet(XLWorkbook wb, string name, int headerRow)
    {
        if (!wb.TryGetWorksheet(name, out var ws))
            throw new InvalidOperationException($"Estimate sheet '{name}' not found.");
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var header = ws.Row(headerRow);
        var lastCol = header.LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (int c = 1; c <= lastCol; c++)
        {
            var norm = Normalize(header.Cell(c).GetString());
            if (norm.Length > 0 && !map.ContainsKey(norm)) map[norm] = c;
        }
        return (ws, map);
    }

    // Lowercase and strip ALL whitespace so embedded '\n' and irregular spacing (e.g. "Qty/\nUnit Work")
    // can't break a match. Slashes/percent/parens are kept, which keeps distinct headers distinct.
    private static string Normalize(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (var ch in s) if (!char.IsWhiteSpace(ch)) buf[n++] = char.ToLowerInvariant(ch);
        return new string(buf[..n]);
    }

    /// <summary>Finds the column whose normalized header contains the normalized needle (first match).</summary>
    private static int? Find(Dictionary<string, int> col, string needle)
    {
        var n = Normalize(needle);
        if (col.TryGetValue(n, out var exact)) return exact;
        foreach (var (k, v) in col) if (k.Contains(n, StringComparison.Ordinal)) return v;
        return null;
    }

    private static int LastRow(IXLWorksheet ws) => ws.LastRowUsed()?.RowNumber() ?? 0;

    private static string? Str(IXLWorksheet ws, int r, Dictionary<string, int> col, string name)
    {
        if (Find(col, name) is not int c) return null;
        var s = ws.Cell(r, c).GetString().Trim();
        return string.IsNullOrEmpty(s) || s is "-" or "—" or "N/A" or "NA" ? null : s;
    }

    private static double? Num(IXLWorksheet ws, int r, Dictionary<string, int> col, string name)
    {
        if (Find(col, name) is not int c) return null;
        var cell = ws.Cell(r, c);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number)
        {
            var v = cell.GetDouble();
            return double.IsFinite(v) ? v : null;
        }
        var s = cell.GetString().Trim();
        if (string.IsNullOrEmpty(s) || s is "-" or "—" or "N/A" or "NA") return null;
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && double.IsFinite(p)
            ? p : null;
    }
}
