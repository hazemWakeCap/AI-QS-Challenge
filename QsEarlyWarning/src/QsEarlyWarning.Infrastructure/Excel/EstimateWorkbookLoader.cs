using QsEarlyWarning.Domain.Estimate;

namespace QsEarlyWarning.Infrastructure.Excel;

/// <summary>
/// Idea-3 estimate source: a thin, project-gated, memoizing wrapper over
/// <see cref="EstimateWorkbookReader"/> (which owns the actual sheet parsing). Bound to one owning
/// project id (resolved once at startup): <see cref="TryLoadForProject"/> returns the memoized model
/// only for that id; any other id — or a missing/invalid workbook — returns null (fail closed). The
/// workbook is a static file, so it is parsed at most once. Behaviour is unchanged from when the parsing
/// lived here directly; the parsing simply moved to the shared reader so the importer can reuse it.
/// </summary>
public sealed class EstimateWorkbookLoader : IEstimateSource
{
    private readonly string _workbookPath;
    private readonly long? _owningProjectId;
    private readonly IEstimateWorkbookReader _reader;
    private readonly object _gate = new();
    private EstimateModel? _cached;
    private bool _loaded;

    /// <param name="workbookPath">Path to Tower_X_Project_Data.xlsx.</param>
    /// <param name="owningProjectId">The project the workbook belongs to (resolved from the estimate
    /// slug at startup); null disables the stress test entirely (fail closed).</param>
    /// <param name="reader">The shared estimate parser (defaults to a new <see cref="EstimateWorkbookReader"/>).</param>
    public EstimateWorkbookLoader(string workbookPath, long? owningProjectId, IEstimateWorkbookReader? reader = null)
    {
        _workbookPath = workbookPath;
        _owningProjectId = owningProjectId;
        _reader = reader ?? new EstimateWorkbookReader();
    }

    public EstimateModel? TryLoadForProject(long projectId)
    {
        if (_owningProjectId is null || projectId != _owningProjectId.Value) return null;

        lock (_gate)
        {
            if (_loaded) return _cached;
            _loaded = true;
            try { _cached = _reader.Read(_workbookPath); }
            catch { _cached = null; } // stress test unavailable; never sink the snapshot
            return _cached;
        }
    }
}
