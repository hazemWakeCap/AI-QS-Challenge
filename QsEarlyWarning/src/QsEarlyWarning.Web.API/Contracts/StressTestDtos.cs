namespace QsEarlyWarning.Web.API.Contracts;

// Idea-3 Estimate Assumption Stress Test — API contracts. Three separated output classes; non-finite
// doubles are sanitized to null in the controller. `available:false` payloads render a clean empty state.

public sealed record ReconciliationFailureDto(
    string Scope, string Check, string? Line, double Actual, double Expected, double Delta, double Tolerance);

public sealed record ReconciliationItemDto(
    string Scope, bool QuantityReDerivationOk, bool ResourceCostIdentityOk, bool RepeatedContractAmtConsistent,
    bool DirectTieOutOk, bool ContractUpliftOk, double DirectTieOutDelta, double ContractUpliftDelta,
    IReadOnlyList<ReconciliationFailureDto> Failures);

public sealed record ReconciliationDto(
    bool Available, bool TiesOut, int ItemsChecked, int ItemsFailed,
    double ProjectDirectDelta, double ProjectUpliftDelta,
    double TotalDirectCost, double TotalIndirectCost, double TotalContractAmt,
    double TotalMargin, double TotalContingency,
    IReadOnlyList<ReconciliationItemDto> FailedItems, IReadOnlyList<string> Notes);

public sealed record AssumptionFlagDto(
    string Package, string? Discipline, string? SubTrade, string? Unit, string? ResourceType,
    string Kind, string Severity, string Reason, int CohortN, string RulesVersion, string? DrivingResourceLine);

public sealed record PackageHeatDto(string Package, string? Discipline, int FlagCount, int HighCount, string Severity);

public sealed record AssumptionsDto(
    bool Available, IReadOnlyList<PackageHeatDto> Heat, IReadOnlyList<AssumptionFlagDto> Flags,
    IReadOnlyList<string> Notes);

public sealed record PeerBenchmarkDto(
    string Package, string? Unit, string? ResourceType, string? ProcurementRoute, string? SubTradeAdvisory,
    double EstimatedUnitCost, double? PeerMedian, double? PeerBandLow, double? PeerBandHigh,
    int PeerCount, double? DeltaPct, string Status);

public sealed record PeerBenchmarkResponseDto(
    bool Available, bool Retrospective, bool Class3NoCellMeetsMinPeers,
    IReadOnlyList<PeerBenchmarkDto> Benchmarks, IReadOnlyList<string> Notes);
