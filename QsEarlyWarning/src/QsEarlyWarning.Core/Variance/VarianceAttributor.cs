using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Variance;

/// <summary>
/// Idea-5 Variance Attribution Bridge — a pure, deterministic engine. For one (BccId, PeriodId) it
/// attributes the cost variance CV = EV − AC to resource categories using the norm-implied resource mix
/// (estimate shares), and reports the schedule/progress lane SV = EV − PV alongside it. It is
/// attribution, NOT cause: it names the dominant resource contributor with a hypothesis label; it never
/// splits CV into quantity vs rate (CV is measured at the earned quantity) or price vs productivity
/// (no hours/quantities/rates in the data).
///
/// The tie-out is the trust anchor: Σ CvR + (ΣAcR − Ac) == CV, exact by construction; any part of AC the
/// four splits don't cover surfaces as UnexplainedResidual, never hidden.
/// </summary>
public sealed class VarianceAttributor
{
    private const double Tol = 0.5; // AED

    // Canonical resource types ↔ the panel's recorded AC split accessors.
    private static readonly (string Type, Func<CostCentrePeriod, double?> Ac)[] Resources =
    {
        ("MANPOWER", p => p.AcManpower), ("MATERIAL", p => p.AcMaterial),
        ("EQUIPMENT", p => p.AcEquipment), ("SUBCONTRACT", p => p.AcSubcontract),
    };

    private static readonly IReadOnlyDictionary<string, string> EvidenceByResource =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MANPOWER"] = "labour hours + wage rates",
            ["MATERIAL"] = "supplier invoices + delivered quantities",
            ["EQUIPMENT"] = "plant hours + hire rates",
            ["SUBCONTRACT"] = "subcontract valuations + agreed scope",
        };

    public VarianceBridge Attribute(
        IReadOnlyList<CostCentrePeriod> panel,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>? mix,
        string bccId, int periodId)
    {
        if (string.IsNullOrWhiteSpace(bccId))
            return VarianceBridge.Unavailable(bccId ?? "", periodId, "bccId is required.");

        var row = panel.FirstOrDefault(p =>
            string.Equals(p.BccId, bccId, StringComparison.OrdinalIgnoreCase) && p.PeriodId == periodId);
        if (row is null)
            return VarianceBridge.Unavailable(bccId, periodId, $"No row for {bccId} at period {periodId}.");

        // G5: EP- packages only (the Postgres loader does not enforce this).
        if (row.PackageCode is not string pkg || !pkg.StartsWith("EP-", StringComparison.OrdinalIgnoreCase))
            return VarianceBridge.Unavailable(bccId, periodId, "Not an EP- estimate package.");

        // G4: finite money + live (EV > 0); never coerce a null to 0.
        if (!Finite(row.EvAed) || !Finite(row.AcCumulative) || !Finite(row.PvAed))
            return VarianceBridge.Unavailable(bccId, periodId, "Missing EV/AC/PV for this row (not diagnosable).");
        double ev = row.EvAed!.Value, ac = row.AcCumulative!.Value, pv = row.PvAed!.Value;
        if (ev <= 0)
            return VarianceBridge.Unavailable(bccId, periodId, "Not started / zero earned value — no meaningful variance.");

        double cv = ev - ac;                       // == recorded CvAed (asserted in tests)
        double sv = ev - pv;
        double? spi = pv > 0 ? ev / pv : null;

        var notes = new List<string>();
        var mixForPkg = mix is not null && mix.TryGetValue(pkg, out var m) ? m : null;

        if (mixForPkg is null || mixForPkg.Count == 0)
        {
            notes.Add("Resource breakdown unavailable — no estimate resource mix for this project; showing CV/SV totals only.");
            return new VarianceBridge(bccId, periodId, Available: true, UnavailableReason: null,
                Package: pkg, Discipline: row.Discipline, Bac: row.BacAed, Pv: pv, Ev: ev, Ac: ac,
                CvAed: cv, SvAed: sv, Spi: spi, Contributions: Array.Empty<ResourceContribution>(),
                DominantResourceType: null, UnexplainedResidual: null,
                TiesOut: Math.Abs(cv - (ev - ac)) <= Tol && Math.Abs(sv - (ev - pv)) <= Tol,
                ResourceBreakdownAvailable: false, AssumptionBased: false, EvidenceNeeded: null, Notes: notes);
        }

        // Normalize shares to sum 1 so EV allocation introduces no leakage (G2).
        double shareTotal = Resources.Sum(r => mixForPkg.TryGetValue(r.Type, out var s) && double.IsFinite(s) ? Math.Max(0, s) : 0);
        var contributions = new List<ResourceContribution>();
        double sumAcR = 0;
        foreach (var (type, acFn) in Resources)
        {
            double rawShare = mixForPkg.TryGetValue(type, out var s) && double.IsFinite(s) ? Math.Max(0, s) : 0;
            double share = shareTotal > 0 ? rawShare / shareTotal : 0;
            double evR = ev * share;
            double acR = acFn(row) is double a && double.IsFinite(a) ? a : 0;
            sumAcR += acR;
            double cvR = evR - acR;
            double? times = evR > 0 ? acR / evR : null;
            contributions.Add(new ResourceContribution(type, Round(share), Round(evR), Round(acR), Round(cvR), Round(times)));
        }

        double residual = sumAcR - ac;             // additive tie-out term (G2): Σ CvR + residual == CV
        bool ties = Math.Abs(cv - (contributions.Sum(c => c.CvR) + residual)) <= Tol
                    && Math.Abs(sv - (ev - pv)) <= Tol;

        // Dominant by variance direction; residual dominates when it outweighs the top resource (G-round2).
        var ranked = contributions
            .OrderBy(c => cv < 0 ? c.CvR : -c.CvR)   // overrun → most negative CvR first; favorable → most positive
            .ToList();
        var topResource = ranked.First();
        string dominant;
        string? evidence;
        if (Math.Abs(residual) > Math.Abs(topResource.CvR))
        {
            dominant = "unexplained residual";
            evidence = "reconcile the recorded AC resource splits (they don't sum to total AC)";
            notes.Add("The four AC splits don't fully cover AC — the unexplained residual outweighs any single resource.");
        }
        else
        {
            dominant = topResource.ResourceType;
            evidence = EvidenceByResource.GetValueOrDefault(dominant);
        }

        notes.Add("Attribution uses estimate resource shares (assumption-based), not measured actuals. Cost is a hypothesis to confirm.");

        return new VarianceBridge(bccId, periodId, Available: true, UnavailableReason: null,
            Package: pkg, Discipline: row.Discipline, Bac: row.BacAed, Pv: Round(pv), Ev: Round(ev), Ac: Round(ac),
            CvAed: Round(cv), SvAed: Round(sv), Spi: Round(spi), Contributions: contributions,
            DominantResourceType: dominant, UnexplainedResidual: Round(residual), TiesOut: ties,
            ResourceBreakdownAvailable: true, AssumptionBased: true, EvidenceNeeded: evidence, Notes: notes);
    }

    private static bool Finite(double? v) => v is double d && double.IsFinite(d);
    private static double Round(double v) => Math.Round(v, 3);
    private static double? Round(double? v) => v is double d && double.IsFinite(d) ? Math.Round(d, 3) : null;
}
