namespace QsEarlyWarning.Infrastructure.Import;

/// <summary>
/// Pure (DB-free) mapping rules used when persisting the estimate graph — extracted so they can be unit
/// tested without a database. These decide values the importer must get right *before* any INSERT, since
/// a bad value would abort the whole import transaction (see WorkbookImporter.PersistEstimate).
/// </summary>
public static class EstimateMapping
{
    /// <summary>Map a workbook Resource Type to the CHECK-constrained set
    /// (<c>MANPOWER|MATERIAL|EQUIPMENT|SUBCONTRACT</c>); null → the row must be skipped, never inserted.</summary>
    public static string? NormalizeRtype(string? t) => t?.Trim().ToUpperInvariant() switch
    {
        "MANPOWER" or "LABOUR" or "LABOR" => "MANPOWER",
        "MATERIAL" or "MATERIALS" => "MATERIAL",
        "EQUIPMENT" or "PLANT" => "EQUIPMENT",
        "SUBCONTRACT" or "SUBCONTRACTOR" or "SUB-CONTRACT" or "SUBCON" => "SUBCONTRACT",
        _ => null,
    };

    /// <summary>Per-resource-line cost, mirroring the GENERATED column
    /// <c>round(coalesce(quantity,0) * coalesce(unit_rate_amount,0), 2)</c>. Nulls behave as 0. A BOQ item's
    /// <c>total_amount</c> is the sum of this over its resource lines (the rollup the DB validates at publish).</summary>
    public static decimal LineCost(double? quantity, double? unitRate)
        => Math.Round((decimal)(quantity ?? 0) * (decimal)(unitRate ?? 0), 2);
}
