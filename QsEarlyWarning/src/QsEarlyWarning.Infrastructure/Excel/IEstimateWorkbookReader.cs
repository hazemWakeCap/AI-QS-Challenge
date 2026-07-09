using QsEarlyWarning.Domain.Estimate;

namespace QsEarlyWarning.Infrastructure.Excel;

/// <summary>
/// Path-based, project-agnostic parse of the estimate graph (workbook sheets 1_BOQ, 2_ESTIMATE_NORMS,
/// 3_BOQ_MAPPING, 4_ESTIMATE_DATASHEET) into an <see cref="EstimateModel"/>. Unlike
/// <see cref="EstimateWorkbookLoader"/> (which is bound to one startup project and memoizes), this reads
/// whatever workbook it's handed — so the importer can persist the estimate graph of any project during
/// import. Throws if a required sheet is missing (caller decides whether that's fatal).
/// </summary>
public interface IEstimateWorkbookReader
{
    EstimateModel Read(string workbookPath);
}
