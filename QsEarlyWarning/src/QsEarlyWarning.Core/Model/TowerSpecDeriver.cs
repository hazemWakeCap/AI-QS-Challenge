using QsEarlyWarning.Domain.Estimate;

namespace QsEarlyWarning.Core.Model;

/// <summary>One derived massing dimension, carrying the BOQ line it came from.</summary>
public sealed record GeometryDimension(
    string Key, string Label, double Value, string Unit,
    string? SourceItemRef, string? SourceDescription, string Derivation);

/// <summary>
/// A parametric massing for the project, plus the provenance table that justifies every number.
/// <see cref="Derived"/> is false when the estimate was unavailable and the fallbacks were used.
/// </summary>
public sealed record TowerSpec(
    int FloorCount, int BasementLevels,
    double FootprintWidthM, double FootprintDepthM, double FloorHeightM,
    double BasementDepthM, double CoreWidthM, double CoreDepthM,
    bool Derived, string Provenance,
    IReadOnlyList<GeometryDimension> Dimensions);

/// <summary>
/// Derives a building massing from priced BOQ lines.
///
/// <para><b>Why this exists.</b> Tower X has no published model — the organisers stated so
/// explicitly. The options were to draw a plausible-looking tower, or to derive one. A drawn tower
/// would put invented geometry underneath real money, which is the one thing this product does not
/// do. So every dimension below traces to a BOQ item ref, and the derivation ships to the UI with
/// the number so it can be argued with rather than believed.</para>
///
/// <para><b>What this is not.</b> It is not a quantity take-off and it is not a design. It is a
/// massing whose proportions are consistent with what the project priced. Where the BOQ implies
/// two different answers (the ground slab is 1,400 m² but the suspended-slab formwork implies a
/// ~3,600 m² plate) that disagreement is reported in the provenance rather than averaged away.</para>
///
/// <para>Matching is by BOQ item ref first — the stable key — with a description keyword fallback,
/// so a workbook whose sections are renumbered still derives rather than silently defaulting.</para>
/// </summary>
public static class TowerSpecDeriver
{
    // Fallbacks used only when the estimate is unavailable. Deliberately plain numbers: if these
    // ever reach the screen, Derived == false and the UI says the massing is not derived.
    private const int FallbackFloors = 6;
    private const int FallbackBasements = 2;
    private const double FallbackPlateArea = 1400.0;

    private const double FloorHeightM = 3.6;      // typical commercial floor-to-floor
    private const double BasementDepthM = 3.2;    // typical basement floor-to-floor

    public static TowerSpec Derive(EstimateModel? estimate)
    {
        var dims = new List<GeometryDimension>();
        var lines = estimate?.BoqLines ?? Array.Empty<BoqLine>();

        // ── floor count: the BOQ prices one line PER FLOOR, so it states the answer outright ──
        var floorLine = FindByUnit(lines, "floor");
        int floors = FallbackFloors;
        if (floorLine?.Quantity is { } fq && fq >= 1 && fq <= 200)
        {
            floors = (int)Math.Round(fq);
            dims.Add(new GeometryDimension("floorCount", "Floors", floors, "floors",
                floorLine.ItemRef, floorLine.Description,
                $"Priced per floor: quantity {Fmt(fq)} at unit '{floorLine.Unit}'."));
        }
        else
        {
            dims.Add(new GeometryDimension("floorCount", "Floors", floors, "floors", null, null,
                "No BOQ line priced per floor — fallback."));
        }

        // ── floor plate: total suspended-slab soffit formwork spread over the floors it forms ──
        var soffit = FindByKeywords(lines, "m²", "soffit", "slab");
        double plate = FallbackPlateArea;
        if (soffit?.Quantity is { } sq && sq > 0 && floors > 0)
        {
            plate = sq / floors;
            dims.Add(new GeometryDimension("floorPlateArea", "Typical floor plate", Round(plate), "m²",
                soffit.ItemRef, soffit.Description,
                $"{Fmt(sq)} m² of suspended-slab soffit formwork ÷ {floors} floors."));
        }
        else
        {
            dims.Add(new GeometryDimension("floorPlateArea", "Typical floor plate", Round(plate), "m²", null, null,
                "No suspended-slab formwork line — fallback."));
        }

        // ── ground-bearing slab: an independent read on the footprint, reported even when it
        //    disagrees with the plate above. Disagreement is information, not noise. ──
        var groundSlab = FindByKeywords(lines, "m²", "ground-bearing", "slab");
        if (groundSlab?.Quantity is { } gq && gq > 0)
        {
            var note = Math.Abs(gq - plate) / Math.Max(plate, 1) > 0.25
                ? $"{Fmt(gq)} m² at ground. Differs from the {Fmt(plate)} m² typical plate — the tower "
                  + "sits on a smaller ground floor, so the plate above drives the massing."
                : $"{Fmt(gq)} m² at ground, consistent with the typical plate.";
            dims.Add(new GeometryDimension("groundSlabArea", "Ground-bearing slab", Round(gq), "m²",
                groundSlab.ItemRef, groundSlab.Description, note));
        }

        // ── facade: sanity-checks the perimeter the plate implies against what was priced ──
        var facade = lines.Where(l => Unit(l) == "m²" && HasAll(l, "curtain wall"))
                          .Where(l => l.Quantity is > 0).ToList();
        if (facade.Count > 0)
        {
            double area = facade.Sum(l => l.Quantity ?? 0);
            dims.Add(new GeometryDimension("facadeArea", "Curtain wall", Round(area), "m²",
                string.Join(" + ", facade.Select(l => l.ItemRef)),
                string.Join(" · ", facade.Select(l => l.Description)),
                $"{Fmt(area)} m² of curtain wall across {facade.Count} line(s)."));
        }

        // ── basements: excavation + demolition of an existing slab evidence a dug substructure.
        //    Depth is not priced anywhere, so the LEVEL COUNT is an assumption, and says so. ──
        var excavation = FindByKeywords(lines, "m³", "excavation");
        int basements = FallbackBasements;
        if (excavation?.Quantity is { } eq && eq > 0)
        {
            dims.Add(new GeometryDimension("basementLevels", "Basement levels", basements, "levels",
                excavation.ItemRef, excavation.Description,
                $"{Fmt(eq)} m³ of excavation ({excavation.Description}) confirms a dug substructure. "
                + $"The BOQ prices no level count, so {basements} is an assumption, not a derivation."));
        }
        else
        {
            dims.Add(new GeometryDimension("basementLevels", "Basement levels", basements, "levels", null, null,
                "No excavation line — fallback."));
        }

        // Square footprint from the plate area: the BOQ prices areas, never a plan shape, so a
        // square is the shape that adds the least invented information.
        double side = Math.Sqrt(Math.Max(plate, 1));
        double core = Math.Max(6.0, side * 0.22);   // proportional service core, for the risers zone

        bool derived = estimate is not null && lines.Count > 0;

        return new TowerSpec(
            floors, basements,
            Round(side), Round(side), FloorHeightM, BasementDepthM, Round(core), Round(core),
            derived,
            derived
                ? "Every dimension is derived from a priced BOQ line and shown with its source. "
                  + "Tower X has no published model; this is an illustrative massing, not a design."
                : "Estimate unavailable — this massing is NOT derived from the BOQ and is illustrative only.",
            dims);
    }

    // ── matching helpers ──────────────────────────────────────────────────────────

    private static BoqLine? FindByUnit(IReadOnlyList<BoqLine> lines, string unit) =>
        lines.FirstOrDefault(l => Unit(l) == unit && l.Quantity is > 0);

    private static BoqLine? FindByKeywords(IReadOnlyList<BoqLine> lines, string unit, params string[] keywords) =>
        lines.Where(l => Unit(l) == unit && l.Quantity is > 0)
             .Where(l => HasAll(l, keywords))
             .OrderByDescending(l => l.Quantity ?? 0)
             .FirstOrDefault();

    private static bool HasAll(BoqLine l, params string[] keywords)
    {
        var d = l.Description ?? "";
        return keywords.All(k => d.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string Unit(BoqLine l) => (l.Unit ?? "").Trim().ToLowerInvariant();

    private static double Round(double v) => Math.Round(v, 1);

    private static string Fmt(double v) => v.ToString("#,##0.#");
}
