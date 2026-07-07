namespace QsEarlyWarning.Domain.Estimate;

/// <summary>
/// Supplies the joined estimate graph (workbook sheets 1-4) for the Estimate Assumption Stress Test.
/// Bound to a single owning project (the workbook belongs to Tower X only): returns the model ONLY for
/// the owning project id, resolved once at startup from <c>Data:EstimateProjectSlug</c>. Any other
/// project id — or an unreadable workbook — yields null (the stress test is simply unavailable, the
/// snapshot is unaffected). Implementations memoize; the workbook is a static file.
/// </summary>
public interface IEstimateSource
{
    EstimateModel? TryLoadForProject(long projectId);
}
