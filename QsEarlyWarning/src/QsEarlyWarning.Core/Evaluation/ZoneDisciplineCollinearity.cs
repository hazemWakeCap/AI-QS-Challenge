using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Evaluation;

/// <summary>One zone and how much trade variety it actually contains.</summary>
public sealed record ZoneComposition(
    string ZoneArea,
    int CentreCount,
    int DisciplineCount,
    IReadOnlyList<string> Disciplines);

/// <summary>
/// Whether `Zone_Area` carries information that `Discipline` does not.
///
/// <para><b>Why this is a computed result and not a footnote.</b> The plan was to test whether
/// physical neighbourhood predicts cost drift, by scoring a centre on how its zone-mates were
/// performing. That test is only meaningful if zone and trade are different things. On Tower X
/// they are not: nearly every zone is a single discipline, and no discipline spans more than one
/// zone — so a "zone-neighbour" feature is a <b>trade-peer</b> feature wearing a spatial costume,
/// and reporting it as spatial would be a false claim whatever the number came out at.</para>
///
/// <para>Publishing this is the honest result. It is measured here, and asserted in a test, so it
/// cannot rot into a stale slide claim.</para>
/// </summary>
public sealed record CollinearityReport(
    int ZoneCount,
    int DisciplineCount,
    /// <summary>Zones containing exactly one discipline — where "where" tells you nothing "who" didn't.</summary>
    int SingleDisciplineZones,
    /// <summary>Disciplines appearing in more than one zone. Zero means zone ⊆ discipline.</summary>
    int DisciplinesSpanningZones,
    /// <summary>The zone with genuine trade mixing, if any — the only place a spatial signal can be isolated.</summary>
    string? MostMixedZone,
    int MostMixedZoneDisciplines,
    IReadOnlyList<ZoneComposition> Zones)
{
    /// <summary>
    /// True when zone adds nothing beyond discipline, i.e. the naive spatial test is unavailable.
    /// </summary>
    public bool ZoneIsProxyForDiscipline => DisciplinesSpanningZones == 0;

    public string Verdict => ZoneIsProxyForDiscipline
        ? $"Zone is a coarsening of discipline on this project: {SingleDisciplineZones} of {ZoneCount} "
          + $"zones hold a single discipline, and none of the {DisciplineCount} disciplines spans more "
          + "than one zone. A zone-neighbour feature therefore measures trade, not space."
        : $"{DisciplinesSpanningZones} of {DisciplineCount} disciplines span more than one zone, so "
          + "zone carries information discipline does not.";
}

public static class ZoneDisciplineCollinearity
{
    /// <summary>
    /// Measures the overlap on the latest period present, where the most centres are live.
    /// One period is enough: cost centres do not move between zones or trades over time.
    /// </summary>
    public static CollinearityReport Measure(IReadOnlyList<CostCentrePeriod> panel)
    {
        if (panel.Count == 0)
            return new CollinearityReport(0, 0, 0, 0, null, 0, Array.Empty<ZoneComposition>());

        int latest = panel.Max(r => r.PeriodId);
        var rows = panel
            .Where(r => r.PeriodId == latest && !string.IsNullOrWhiteSpace(r.ZoneArea))
            .ToList();

        var zones = rows
            .GroupBy(r => r.ZoneArea!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var disciplines = g
                    .Select(r => (r.Discipline ?? "(unknown)").Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(d => d, StringComparer.Ordinal)
                    .ToList();
                return new ZoneComposition(g.Key, g.Count(), disciplines.Count, disciplines);
            })
            .OrderByDescending(z => z.CentreCount)
            .ToList();

        var allDisciplines = rows
            .Select(r => (r.Discipline ?? "(unknown)").Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int spanning = allDisciplines.Count(d =>
            rows.Where(r => string.Equals((r.Discipline ?? "(unknown)").Trim(), d, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.ZoneArea!.Trim().ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .Count() > 1);

        var mixed = zones.OrderByDescending(z => z.DisciplineCount).FirstOrDefault();

        return new CollinearityReport(
            zones.Count,
            allDisciplines.Count,
            zones.Count(z => z.DisciplineCount == 1),
            spanning,
            mixed?.DisciplineCount > 1 ? mixed.ZoneArea : null,
            mixed?.DisciplineCount ?? 0,
            zones);
    }
}
