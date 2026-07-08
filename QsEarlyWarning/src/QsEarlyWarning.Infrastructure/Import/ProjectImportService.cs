namespace QsEarlyWarning.Infrastructure.Import;

/// <summary>
/// Async front door to the (synchronous, blocking) <see cref="IWorkbookImporter"/> for the Web API. Captures
/// the connection string so controllers don't need the DI-private value, and offloads the blocking import to
/// the thread pool. Used by both "create project from workbook" and "re-import / refresh".
/// </summary>
public sealed class ProjectImportService
{
    private readonly string _connectionString;
    private readonly IWorkbookImporter _importer;

    public ProjectImportService(string connectionString, IWorkbookImporter importer)
    {
        _connectionString = connectionString;
        _importer = importer;
    }

    public Task<ReconciliationReport> ImportAsync(
        string workbookPath, string slug, string actor, ProjectMeta meta, CancellationToken ct = default)
        => Task.Run(() => _importer.Import(workbookPath, _connectionString, slug, actor, meta), ct);
}
