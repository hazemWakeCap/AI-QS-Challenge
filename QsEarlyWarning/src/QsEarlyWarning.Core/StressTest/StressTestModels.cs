namespace QsEarlyWarning.Core.StressTest;

// Idea-3 Estimate Assumption Stress Test — result model. Three explicitly separated output classes,
// never fused into one score (see the integration plan): Class 1 arithmetic reconciliation tie-out
// (a correctness PROOF, not a signal), Class 2 estimate-side assumption flags (reads zero actuals),
// Class 3 retrospective-only gated peer benchmark. All amounts AED.

/// <summary>One failed Class-1 conjunct with its numbers, so a FAIL is always explained (never bare).</summary>
public sealed record ReconciliationFailure(
    string Scope, string Check, string? Line, double Actual, double Expected, double Delta, double Tolerance);

/// <summary>Class 1 — per BOQ item. Every conjunct is its own boolean; <see cref="TiesOut"/> is their AND.</summary>
public sealed record ReconciliationResult(
    string Scope,
    bool QuantityReDerivationOk, bool ResourceCostIdentityOk, bool RepeatedContractAmtConsistent,
    bool DirectTieOutOk, bool ContractUpliftOk,
    double DirectCost, double IndirectCost, double DirectTieOutDelta,
    double TotalContractAmt, double ContractUplift, double ContractUpliftDelta,
    bool TiesOut, double AbsPct,
    IReadOnlyList<ReconciliationFailure> Failures);

/// <summary>Class 1 rollup. <c>TiesOut</c> requires every item to tie out AND both project deltas ≤ 1 AED.</summary>
public sealed record ReconciliationSummary(
    bool TiesOut, int ItemsChecked, int ItemsFailed,
    double ProjectDirectDelta, double ProjectUpliftDelta,
    double TotalDirectCost, double TotalIndirectCost, double TotalContractAmt,
    double TotalMargin, double TotalContingency,
    IReadOnlyList<ReconciliationResult> Items);

/// <summary>Class 2 — one estimate-side assumption flagged for QS review (a prompt, not a verdict).</summary>
public sealed record AssumptionFlag(
    string Package, string? Discipline, string? SubTrade, string? Unit, string? ResourceType,
    string Kind, string Severity, string Reason, int CohortN, string RulesVersion,
    string? DrivingResourceLine, double? EstimatedUnitCost);

/// <summary>Class 2 heatmap cell — assumption-flag severity aggregated per package × discipline.</summary>
public sealed record PackageHeatCell(
    string Package, string? Discipline, int FlagCount, int HighCount, string Severity);

/// <summary>Class 3 — retrospective peer benchmark for a package-cell (unit + resource type + route).</summary>
public sealed record PeerBenchmark(
    string Package, string? Unit, string? ResourceType, string? ProcurementRoute, string? SubTradeAdvisory,
    double EstimatedUnitCost, double? PeerMedian, double? PeerBandLow, double? PeerBandHigh,
    int PeerCount, double? DeltaPct, string Status);

/// <summary>The full stress-test report hung on the project snapshot.</summary>
public sealed record StressTestReport(
    bool Available, string GeneratedForProject,
    ReconciliationSummary Reconciliation,
    IReadOnlyList<AssumptionFlag> AssumptionFlags,
    IReadOnlyList<PeerBenchmark> PeerBenchmarks,
    IReadOnlyList<PackageHeatCell> PackageHeat,
    bool Class3NoCellMeetsMinPeers,
    IReadOnlyList<string> Notes);
