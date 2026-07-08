using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Domain.Estimate;

namespace QsEarlyWarning.Core.StressTest;

/// <summary>
/// Idea-3 Estimate Assumption Stress Test — a deterministic engine (no ML). It rebuilds every estimate
/// package from norms × rates and emits three explicitly separated output classes (see the integration
/// plan). Pure and deterministic: the same workbook yields a byte-identical report.
///
///  • Class 1 — arithmetic reconciliation tie-out (correctness PROOF, not a signal). Recomputes
///    Total Resource Qty = BOQ Qty × Qty/Unit Work ÷ Output Norm (uniform across resource types) and
///    reconciles the direct+indirect build-up to the BOQ; the residual is exactly margin + contingency.
///  • Class 2 — estimate-side assumption flags (reads ZERO actuals): aggressive Output Norm, thin Unit
///    Rate, thin/zero contingency — review prompts, cohort-gated, with exact thresholds.
///  • Class 3 — RETROSPECTIVE-ONLY gated peer benchmark at the package-cell grain, leave-one-out,
///    suppressed below 5 distinct peer packages (same-project peers don't exist at award).
/// </summary>
public sealed class EstimateStressTester
{
    public const string RulesVersion = "v1";
    public const int MinCohortN = 5;
    public const int MinPeerN = 5;

    // Class-1 tolerances (G0): quantity relative, money absolute per item, project rollup absolute.
    public const double QtyRelTol = 1e-6;
    public const double MoneyAbsTol = 0.01;
    public const double RollupAbsTol = 1.0;

    // Class-2 thresholds (G12, percentage points for contingency).
    private const double OutputNormTopP = 0.90;
    private const double UnitRateBottomP = 0.10;
    private const double ContThinThreshold = 2.0;

    // Class-3 band + completion gate (G6, G13).
    private const double PeerBandLowP = 0.25;
    private const double PeerBandHighP = 0.75;
    private const double CompletionPct = 100.0;

    private static readonly (string Rtype, Func<CostCentrePeriod, double?> Ac)[] ResourceAc =
    {
        ("MANPOWER", p => p.AcManpower), ("MATERIAL", p => p.AcMaterial),
        ("EQUIPMENT", p => p.AcEquipment), ("SUBCONTRACT", p => p.AcSubcontract),
    };

    public StressTestReport Run(EstimateModel estimate, IReadOnlyList<CostCentrePeriod>? panel,
        string generatedForProject = "tower-x")
    {
        var notes = new List<string>();
        var reconciliation = Reconcile(estimate);
        var (flags, heat) = Class2(estimate);
        var (benchmarks, class3Empty) = Class3(estimate, panel, notes);

        notes.Insert(0, "Class 1 is a correctness proof (the estimate reconciles by construction), not a signal.");
        notes.Add("Class 2 flags are review prompts (estimate-side, zero actuals), not verdicts.");
        notes.Add("Class 3 is RETROSPECTIVE validation only — same-project peers do not exist at award.");

        return new StressTestReport(
            Available: true, GeneratedForProject: generatedForProject,
            Reconciliation: reconciliation, AssumptionFlags: flags, PeerBenchmarks: benchmarks,
            PackageHeat: heat, Class3NoCellMeetsMinPeers: class3Empty, Notes: notes);
    }

    // ── Class 1: arithmetic reconciliation tie-out (G0, G1, G2) ──

    private static ReconciliationSummary Reconcile(EstimateModel est)
    {
        var items = new List<ReconciliationResult>();
        double sumDirect = 0, sumIndirect = 0, sumContract = 0, sumMargin = 0, sumCont = 0, sumBoqDirectInd = 0;

        foreach (var boq in est.BoqLines)
        {
            if (!est.ResourceLinesByItemRef.TryGetValue(boq.ItemRef, out var lines) || lines.Count == 0)
                continue;

            var failures = new List<ReconciliationFailure>();
            double directCost = 0, indirectCost = 0;
            bool qtyOk = true, costIdOk = true;

            foreach (var line in lines)
            {
                directCost += line.ResourceCost ?? 0;
                indirectCost += line.IndirectCost ?? 0;

                // (a) quantity re-derivation — only where all inputs are present.
                var norm = line.NormCode is not null && est.NormByCode.TryGetValue(line.NormCode, out var n) ? n : null;
                if (line.BoqQty is double q && line.QtyPerUnitWork is double qpu &&
                    norm?.OutputNorm is double on && on != 0 && line.TotalResourceQty is double trq)
                {
                    var recomputed = q * qpu / on;
                    if (Math.Abs(recomputed - trq) > QtyRelTol * Math.Max(1, Math.Abs(trq)))
                    {
                        qtyOk = false;
                        failures.Add(new ReconciliationFailure(boq.ItemRef, "QuantityReDerivation",
                            $"{line.ResourceType} {line.NormCode}", trq, recomputed, recomputed - trq,
                            QtyRelTol * Math.Max(1, Math.Abs(trq))));
                    }
                }

                // (b) resource-cost identity: Resource Cost == Total Resource Qty × Unit Rate.
                if (line.TotalResourceQty is double t2 && line.UnitRate is double rate && line.ResourceCost is double rc)
                {
                    var expected = t2 * rate;
                    if (Math.Abs(rc - expected) > MoneyAbsTol)
                    {
                        costIdOk = false;
                        failures.Add(new ReconciliationFailure(boq.ItemRef, "ResourceCostIdentity",
                            $"{line.ResourceType} {line.NormCode}", rc, expected, rc - expected, MoneyAbsTol));
                    }
                }
            }

            // (e) repeated Total Contract Amt consistent across the item's rows; dedup → one value.
            var contractVals = lines.Where(l => l.TotalContractAmt is double v && double.IsFinite(v))
                .Select(l => l.TotalContractAmt!.Value).ToList();
            double contractAmt = contractVals.Count > 0 ? contractVals[0] : 0;
            bool repeatedOk = contractVals.Count == 0 || (contractVals.Max() - contractVals.Min()) <= MoneyAbsTol;
            if (!repeatedOk)
                failures.Add(new ReconciliationFailure(boq.ItemRef, "RepeatedContractAmtConsistent", null,
                    contractVals.Max(), contractVals.Min(), contractVals.Max() - contractVals.Min(), MoneyAbsTol));

            // (c) direct tie-out and contract uplift.
            var boqDirectInd = boq.DirectIndirectAmount;
            var margin = boq.MarginAmount ?? 0;
            var cont = boq.ContingencyAmount ?? 0;
            double directTieOutDelta = 0; bool directOk;
            if (boqDirectInd is double di)
            {
                directTieOutDelta = (directCost + indirectCost) - di;
                directOk = Math.Abs(directTieOutDelta) <= MoneyAbsTol;
                if (!directOk)
                    failures.Add(new ReconciliationFailure(boq.ItemRef, "DirectTieOut", null,
                        directCost + indirectCost, di, directTieOutDelta, MoneyAbsTol));
            }
            else { directOk = false; failures.Add(new ReconciliationFailure(boq.ItemRef, "DirectTieOut", null, directCost + indirectCost, 0, 0, MoneyAbsTol)); }

            double contractUplift = contractAmt - (directCost + indirectCost);
            double contractUpliftDelta = contractUplift - (margin + cont);
            bool upliftOk = Math.Abs(contractUpliftDelta) <= MoneyAbsTol;
            if (!upliftOk)
                failures.Add(new ReconciliationFailure(boq.ItemRef, "ContractUplift", null,
                    contractUplift, margin + cont, contractUpliftDelta, MoneyAbsTol));

            bool ties = qtyOk && costIdOk && repeatedOk && directOk && upliftOk;
            double absPct = boqDirectInd is double d2 && d2 != 0 ? Math.Abs(directTieOutDelta) / Math.Abs(d2) * 100 : 0;
            var pkg = est.MappingByItemRef.TryGetValue(boq.ItemRef, out var mp) ? mp.EstimatePackage : null;

            items.Add(new ReconciliationResult(boq.ItemRef, pkg, qtyOk, costIdOk, repeatedOk, directOk, upliftOk,
                directCost, indirectCost, directTieOutDelta, contractAmt, contractUplift, contractUpliftDelta,
                ties, absPct, failures));

            sumDirect += directCost; sumIndirect += indirectCost; sumContract += contractAmt;
            sumMargin += margin; sumCont += cont; sumBoqDirectInd += boqDirectInd ?? 0;
        }

        double projectDirectDelta = (sumDirect + sumIndirect) - sumBoqDirectInd;
        double projectUpliftDelta = (sumContract - (sumDirect + sumIndirect)) - (sumMargin + sumCont);
        int failed = items.Count(i => !i.TiesOut);
        bool overallTies = failed == 0
            && Math.Abs(projectDirectDelta) <= RollupAbsTol && Math.Abs(projectUpliftDelta) <= RollupAbsTol;

        return new ReconciliationSummary(overallTies, items.Count, failed, projectDirectDelta, projectUpliftDelta,
            sumDirect, sumIndirect, sumContract, sumMargin, sumCont, items);
    }

    // ── Class 2: estimate-side assumption flags (G3, G7, G10, G12) ──

    private static (IReadOnlyList<AssumptionFlag>, IReadOnlyList<PackageHeatCell>) Class2(EstimateModel est)
    {
        var flags = new List<AssumptionFlag>();

        // Output Norm — top decile within (sub-trade + unit) cohort.
        var normCohorts = est.Norms.Where(n => n.OutputNorm is double v && double.IsFinite(v))
            .GroupBy(n => (n.SubTradeCode ?? "", n.Unit ?? ""));
        foreach (var cohort in normCohorts)
        {
            var members = cohort.ToList();
            if (members.Count < MinCohortN) continue;
            var p90 = Quantile(members.Select(m => m.OutputNorm!.Value), OutputNormTopP);
            foreach (var norm in members.Where(m => m.OutputNorm!.Value >= p90))
                foreach (var pkg in PackagesUsingNorm(est, norm.NormCode))
                    flags.Add(new AssumptionFlag(pkg, norm.DisciplineName, norm.SubTradeName, norm.Unit, null,
                        "OutputNormTopPercentile", "medium",
                        $"Output Norm {norm.OutputNorm:0.##} ≥ P90 ({p90:0.##}) for {norm.SubTradeName ?? norm.SubTradeCode}/{norm.Unit} (aggressive productivity assumption)",
                        members.Count, RulesVersion, $"norm {norm.NormCode}", null,
                        ItemRefsForNormInPackage(est, norm.NormCode, pkg)));
        }

        // Unit Rate — bottom decile within (resource type + description + consumption unit) cohort.
        var rateCohorts = est.ResourceLines.Where(l => l.UnitRate is double v && double.IsFinite(v) && l.Package is not null)
            .GroupBy(l => (l.ResourceType, l.ResourceDescription ?? "", l.ConsumptionUnit ?? ""));
        foreach (var cohort in rateCohorts)
        {
            var members = cohort.ToList();
            if (members.Count < MinCohortN) continue;
            var p10 = Quantile(members.Select(m => m.UnitRate!.Value), UnitRateBottomP);
            foreach (var line in members.Where(m => m.UnitRate!.Value <= p10))
            {
                var norm = line.NormCode is not null && est.NormByCode.TryGetValue(line.NormCode, out var n) ? n : null;
                flags.Add(new AssumptionFlag(line.Package!, norm?.DisciplineName ?? DiscFromPackage(line.Package!),
                    norm?.SubTradeName, line.Unit, line.ResourceType, "UnitRateBottomOfBand", "medium",
                    $"Unit rate {line.UnitRate:0.##} ≤ P10 ({p10:0.##}) for {line.ResourceType}/{line.ResourceDescription} (thin rate)",
                    members.Count, RulesVersion, $"{line.ItemRef} {line.ResourceDescription}", null, new[] { line.ItemRef }));
            }
        }

        // Contingency — zero (high) / thin (medium), mutually exclusive; cohort is the whole BOQ (all items).
        foreach (var boq in est.BoqLines.Where(b => b.ContPct is double v && double.IsFinite(v)))
        {
            var pkg = est.MappingByItemRef.TryGetValue(boq.ItemRef, out var m) ? m.EstimatePackage : null;
            if (pkg is null) continue;
            var cp = boq.ContPct!.Value;
            var norm = boq.NormRef is not null && est.NormByCode.TryGetValue(boq.NormRef, out var nn) ? nn : null;
            if (cp == 0)
                flags.Add(new AssumptionFlag(pkg, norm?.DisciplineName ?? DiscFromPackage(pkg), norm?.SubTradeName,
                    boq.Unit, null, "ZeroContingency", "high", $"Contingency is 0% on item {boq.ItemRef}",
                    est.BoqLines.Count, RulesVersion, $"item {boq.ItemRef}", null, new[] { boq.ItemRef }));
            else if (cp is > 0 and < ContThinThreshold)
                flags.Add(new AssumptionFlag(pkg, norm?.DisciplineName ?? DiscFromPackage(pkg), norm?.SubTradeName,
                    boq.Unit, null, "ThinContingency", "medium", $"Contingency {cp:0.##}% (< {ContThinThreshold}%) on item {boq.ItemRef}",
                    est.BoqLines.Count, RulesVersion, $"item {boq.ItemRef}", null, new[] { boq.ItemRef }));
        }

        var heat = flags.GroupBy(f => f.Package)
            .Select(g =>
            {
                int high = g.Count(f => f.Severity == "high");
                return new PackageHeatCell(g.Key, g.Select(f => f.Discipline).FirstOrDefault(d => d is not null) ?? DiscFromPackage(g.Key),
                    g.Count(), high, high > 0 ? "high" : "medium");
            })
            .OrderByDescending(c => c.HighCount).ThenByDescending(c => c.FlagCount).ThenBy(c => c.Package)
            .ToList();

        flags = flags.OrderBy(f => f.Package, StringComparer.Ordinal).ThenBy(f => f.Kind).ThenBy(f => f.DrivingResourceLine).ToList();
        return (flags, heat);
    }

    // ── Class 3: retrospective package-cell peer benchmark (G4, G5, G6, G11, G13) ──

    private static (IReadOnlyList<PeerBenchmark>, bool NoCellMeetsMin) Class3(
        EstimateModel est, IReadOnlyList<CostCentrePeriod>? panel, List<string> notes)
    {
        if (panel is null)
        {
            notes.Add("Class 3 skipped: no actuals panel for this project.");
            return (Array.Empty<PeerBenchmark>(), true);
        }

        // Items whose unit is ambiguous across sheets are excluded (G11).
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? UnitOf(string itemRef)
        {
            var mapUnit = est.MappingByItemRef.TryGetValue(itemRef, out var m) ? m.Unit : null;
            var boqUnit = est.BoqByItemRef.TryGetValue(itemRef, out var b) ? b.Unit : null;
            if (mapUnit is not null && boqUnit is not null && !string.Equals(mapUnit, boqUnit, StringComparison.OrdinalIgnoreCase))
            { excluded.Add(itemRef); return null; }
            return mapUnit ?? boqUnit;
        }
        string? RouteOf(string itemRef) => est.MappingByItemRef.TryGetValue(itemRef, out var m) ? m.Procurement : null;
        string? PkgOf(string itemRef) => est.MappingByItemRef.TryGetValue(itemRef, out var m) ? m.EstimatePackage : null;
        string? SubTradeOf(string itemRef)
        {
            var nc = est.MappingByItemRef.TryGetValue(itemRef, out var m) ? m.NormCode : null;
            return nc is not null && est.NormByCode.TryGetValue(nc, out var n) ? n.SubTradeName : null;
        }

        // Estimate cells: (package, unit, rtype, route) → Σ(resource+indirect) / Σ distinct item qty.
        var estCost = new Dictionary<Cell, double>();
        var estItemQty = new Dictionary<Cell, Dictionary<string, double>>();
        var estSubTrade = new Dictionary<Cell, string?>();
        foreach (var line in est.ResourceLines)
        {
            var pkg = line.Package ?? PkgOf(line.ItemRef);
            var unit = UnitOf(line.ItemRef);
            var route = RouteOf(line.ItemRef);
            if (pkg is null || unit is null) continue;
            var itemQty = est.BoqByItemRef.TryGetValue(line.ItemRef, out var b) ? b.Quantity : line.BoqQty;
            if (itemQty is not double qty || !double.IsFinite(qty) || qty <= 0) continue;

            var cell = new Cell(pkg, unit, Norm(line.ResourceType), route ?? "");
            estCost[cell] = estCost.GetValueOrDefault(cell) + (line.ResourceCost ?? 0) + (line.IndirectCost ?? 0);
            if (!estItemQty.TryGetValue(cell, out var qmap)) estItemQty[cell] = qmap = new();
            qmap[line.ItemRef] = qty;
            estSubTrade.TryAdd(cell, SubTradeOf(line.ItemRef));
        }

        // Realized cells from completed BCCs (latest row per BCC, then require ActualPctComplete >= 100).
        int unmatched = 0;
        var realizedAc = new Dictionary<Cell, double>();
        var realizedQty = new Dictionary<Cell, double>();
        var latest = panel.GroupBy(p => p.BccId)
            .Select(g => g.OrderByDescending(p => p.PeriodId).First())
            .Where(p => p.ActualPctComplete is double a && a >= CompletionPct);
        foreach (var bcc in latest)
        {
            var itemRef = bcc.WbsCode;
            if (itemRef is null || !est.MappingByItemRef.ContainsKey(itemRef)) { unmatched++; continue; }
            var unit = UnitOf(itemRef);
            var pkg = PkgOf(itemRef);
            var route = RouteOf(itemRef);
            if (unit is null || pkg is null) { unmatched++; continue; }
            if (bcc.EarnedQtyCumul is not double earned || !double.IsFinite(earned) || earned <= 0) continue;

            foreach (var (rtype, acFn) in ResourceAc)
            {
                if (acFn(bcc) is not double ac || !double.IsFinite(ac) || ac < 0) continue; // zero AC kept
                var cell = new Cell(pkg, unit, rtype, route ?? "");
                realizedAc[cell] = realizedAc.GetValueOrDefault(cell) + ac;
                realizedQty[cell] = realizedQty.GetValueOrDefault(cell) + earned;
            }
        }
        // Package-cell realized unit cost (one observation per package), dropping all-zero-AC cells.
        var realizedUnitCost = new Dictionary<Cell, double>();
        foreach (var (cell, ac) in realizedAc)
            if (ac > 0 && realizedQty.TryGetValue(cell, out var q) && q > 0)
                realizedUnitCost[cell] = ac / q;

        if (realizedUnitCost.Count == 0)
            notes.Add("Class 3: no completed centre (Actual % ≥ 100 with a positive earned quantity) exists " +
                      "in this project's actuals, so there is nothing to benchmark against — every cell has 0 peers.");

        // Benchmark each estimate cell against peer package-cells (LOO on package).
        var benchmarks = new List<PeerBenchmark>();
        foreach (var (cell, cost) in estCost)
        {
            var denom = estItemQty[cell].Values.Sum();
            if (denom <= 0) continue;
            var estUnit = cost / denom;
            if (!double.IsFinite(estUnit) || estUnit <= 0) continue;

            // peers: other packages' realized cells with same (unit, rtype, route)
            var peers = realizedUnitCost
                .Where(kv => kv.Key.Package != cell.Package && kv.Key.Unit == cell.Unit
                             && kv.Key.Rtype == cell.Rtype && kv.Key.Route == cell.Route)
                .Select(kv => kv.Value).ToList();

            if (peers.Count >= MinPeerN)
            {
                var median = Quantile(peers, 0.5);
                var low = Quantile(peers, PeerBandLowP);
                var high = Quantile(peers, PeerBandHighP);
                var delta = median != 0 ? (estUnit - median) / median * 100 : 0;
                benchmarks.Add(new PeerBenchmark(cell.Package, cell.Unit, cell.Rtype, cell.Route,
                    estSubTrade.GetValueOrDefault(cell), estUnit, median, low, high, peers.Count, delta, "Benchmarked"));
            }
            else
            {
                benchmarks.Add(new PeerBenchmark(cell.Package, cell.Unit, cell.Rtype, cell.Route,
                    estSubTrade.GetValueOrDefault(cell), estUnit, null, null, null, peers.Count, null, "Suppressed"));
            }
        }

        if (excluded.Count > 0) notes.Add($"Class 3 excluded {excluded.Count} item(s) with an ambiguous unit across sheets.");
        if (unmatched > 0) notes.Add($"Class 3 excluded {unmatched} completed centre(s) not matching a single estimate item.");

        benchmarks = benchmarks.OrderBy(b => b.Package, StringComparer.Ordinal)
            .ThenBy(b => b.Unit).ThenBy(b => b.ResourceType).ToList();
        bool noneMeetMin = !benchmarks.Any(b => b.Status == "Benchmarked");
        if (noneMeetMin) notes.Add("Class 3: no cell meets the 5-peer minimum on this single-project workbook.");
        return (benchmarks, noneMeetMin);
    }

    // ── helpers ──

    private readonly record struct Cell(string Package, string Unit, string Rtype, string Route);

    private static string Norm(string rtype) => rtype.Trim().ToUpperInvariant();

    private static IEnumerable<string> PackagesUsingNorm(EstimateModel est, string normCode) =>
        est.Mappings.Where(m => string.Equals(m.NormCode, normCode, StringComparison.OrdinalIgnoreCase)
                                && m.EstimatePackage is not null)
            .Select(m => m.EstimatePackage!).Distinct(StringComparer.Ordinal);

    /// <summary>The BOQ item refs behind an OutputNorm flag: the package's items that use that norm.</summary>
    private static IReadOnlyList<string> ItemRefsForNormInPackage(EstimateModel est, string normCode, string pkg) =>
        est.Mappings.Where(m => string.Equals(m.NormCode, normCode, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(m.EstimatePackage, pkg, StringComparison.Ordinal))
            .Select(m => m.ItemRef).Distinct(StringComparer.Ordinal).ToList();

    private static string? DiscFromPackage(string pkg)
    {
        // "EP-CIV-DEMO" → "CIV"
        var parts = pkg.Split('-');
        return parts.Length >= 2 ? parts[1] : null;
    }

    /// <summary>Type-7 (linear-interpolation) quantile on an ascending-sorted copy. p in [0,1].</summary>
    public static double Quantile(IEnumerable<double> values, double p)
    {
        var v = values.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (v.Length == 0) return double.NaN;
        if (v.Length == 1) return v[0];
        var h = (v.Length - 1) * p;
        var lo = (int)Math.Floor(h);
        var frac = h - lo;
        return lo + 1 < v.Length ? v[lo] + frac * (v[lo + 1] - v[lo]) : v[^1];
    }
}
