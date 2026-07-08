namespace QsEarlyWarning.Web.API.Contracts;

// Idea-5 Variance Attribution Bridge — API contracts. Mirrors the core bridge; non-finite doubles → null.
// `available:false` (200) represents a missing / non-EP / non-live / null-money row honestly.

public sealed record ResourceContributionDto(
    string ResourceType, double NormShare, double EvR, double AcR, double CvR, double? TimesNormBudget);

public sealed record VarianceBridgeDto(
    string BccId, int PeriodId, bool Available, string? UnavailableReason,
    string? Package, string? Discipline,
    double? Bac, double? Pv, double? Ev, double? Ac, double? CvAed, double? SvAed, double? Spi,
    IReadOnlyList<ResourceContributionDto> Contributions, string? DominantResourceType,
    double? UnexplainedResidual, bool TiesOut, bool ResourceBreakdownAvailable,
    bool AssumptionBased, string? EvidenceNeeded, IReadOnlyList<string> Notes);
