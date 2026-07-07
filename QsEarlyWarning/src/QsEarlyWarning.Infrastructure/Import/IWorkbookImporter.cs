namespace QsEarlyWarning.Infrastructure.Import;

/// <summary>
/// Imports Tower_X_Project_Data.xlsx (9_HISTORICAL_DATA) into the Postgres system of record and
/// reconciles the DB-computed EVM against the workbook's recorded EVM (plan Phase 1 / §5c).
/// </summary>
public interface IWorkbookImporter
{
    /// <param name="workbookPath">Path to the .xlsx.</param>
    /// <param name="connectionString">Npgsql connection to a schema built by db/migrations.</param>
    /// <param name="projectSlug">Tenant slug to (re)load; a prior load under this slug is purged first.</param>
    /// <param name="actor">Who ran the import (recorded in import_runs).</param>
    ReconciliationReport Import(string workbookPath, string connectionString, string projectSlug, string actor);
}
