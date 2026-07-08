namespace QsEarlyWarning.Infrastructure.Import;

/// <summary>The project identity to (re)create on import: display name, ISO reporting currency, and the
/// owning user that gets the RLS <c>owner</c> membership. Lets the same pipeline serve the Tower-X CLI
/// import and an in-app "create project from workbook" flow.</summary>
public sealed record ProjectMeta(string Name, string Currency, long OwnerUserId);

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

    /// <summary>As <see cref="Import(string,string,string,string)"/> but with explicit project metadata
    /// (name / currency / owner) instead of the Tower-X defaults.</summary>
    ReconciliationReport Import(string workbookPath, string connectionString, string projectSlug, string actor, ProjectMeta meta);
}
