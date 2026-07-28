namespace QsEarlyWarning.Core.Model;

/// <summary>What was measured off an element: its volume or its area.</summary>
public enum TakeoffMeasure
{
    Volume,
    Area,
}

/// <summary>
/// One declared rule binding an IFC class + measure to a BOQ item.
/// </summary>
/// <param name="IfcClass">Upper-case IFC entity name, e.g. IFCCOLUMN.</param>
/// <param name="Measure">Which measurement this rule consumes.</param>
/// <param name="Unit">The unit the measurement arrives in — must match the BOQ item's unit.</param>
/// <param name="BoqItemRef">The BOQ item whose rate prices it.</param>
/// <param name="Rationale">Why this pairing is defensible, shown in the UI.</param>
public sealed record TakeoffRule(
    string IfcClass,
    TakeoffMeasure Measure,
    string Unit,
    string BoqItemRef,
    string Rationale);

/// <summary>
/// The IFC-class → BOQ-item rules used to price a model take-off.
///
/// <para><b>This table is the argument, so it ships to the UI.</b> Every rule is a judgement — that
/// an `IfcColumn`'s volume is fairly priced by the BOQ's column-concrete rate — and a QS must be
/// able to disagree with it. Burying it in code would make the resulting number unauditable, which
/// is the opposite of what this product is for.</para>
///
/// <para><b>The gaps are deliberate and are not filled with approximations.</b> `IfcBeam` has no
/// rule: Tower X's BOQ prices no beam concrete, and pricing 358.8 m³ of beams at the slab rate
/// would invent a number the estimate never contained. `IfcReinforcingBar` has no rule either —
/// Tower X prices rebar by the tonne while a model carries bar geometry, and converting one to the
/// other needs a steel density and a bar schedule the file does not have. Both land in the unpriced
/// residual, where they are visible.</para>
/// </summary>
public static class TakeoffRateMap
{
    public static readonly IReadOnlyList<TakeoffRule> Rules = new[]
    {
        new TakeoffRule("IFCCOLUMN", TakeoffMeasure.Volume, "m³", "2.04",
            "Column concrete, priced per m³ of column."),
        new TakeoffRule("IFCWALL", TakeoffMeasure.Volume, "m³", "2.05",
            "Structural wall concrete, priced per m³ of wall."),
        new TakeoffRule("IFCSLAB", TakeoffMeasure.Volume, "m³", "2.06",
            "Suspended slab concrete, priced per m³ of slab."),
        new TakeoffRule("IFCSLAB", TakeoffMeasure.Area, "m²", "2.11",
            "Slab soffit formwork, priced per m² of slab face."),
    };

    /// <summary>The rule for a class + measure, or null when the pairing is deliberately unpriced.</summary>
    public static TakeoffRule? Find(string ifcClass, TakeoffMeasure measure) =>
        Rules.FirstOrDefault(r =>
            string.Equals(r.IfcClass, ifcClass, StringComparison.OrdinalIgnoreCase) &&
            r.Measure == measure);

    /// <summary>
    /// Why a class has no rule, in words a QS can act on. Returns null for classes we do price.
    /// </summary>
    public static string? WhyUnpriced(string ifcClass) => ifcClass.ToUpperInvariant() switch
    {
        "IFCBEAM" =>
            "Measured, but this rate library prices no beam concrete — the BOQ has no beam item. "
            + "Pricing it at the slab rate would invent a number the estimate never contained.",
        "IFCREINFORCINGBAR" =>
            "This library prices rebar by the tonne; converting bar geometry to tonnage needs a "
            + "steel density and a bar schedule this model does not carry.",
        "IFCMEMBER" or "IFCPLATE" =>
            "No rate in this library for this element class.",
        _ => null,
    };
}
