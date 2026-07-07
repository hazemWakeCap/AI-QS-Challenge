namespace QsEarlyWarning.Infrastructure.Crud;

public enum ColKind { Text, Numeric, Int, Bigint, Bool, Date }

/// <summary>One editable/displayable column of a CRUD entity.</summary>
public sealed record Column(
    string Name, ColKind Kind, bool Insertable = true, bool Updatable = true,
    bool Required = false, string? FkEntity = null, string[]? Enum = null);

public sealed record Capabilities(bool List, bool Get, bool Create, bool Update, bool Delete);

/// <summary>Metadata for one CRUD-able table — the single source of truth for the API and the
/// auto-generated admin UI. Column names here are the ONLY names the generic service will put into
/// SQL (a whitelist). `id`/`project_id`/generated columns are handled by the service, not listed.</summary>
public sealed record EntityDescriptor(
    string Key, string Table, string Display, IReadOnlyList<Column> Columns,
    IReadOnlyList<string> NaturalKey, Capabilities Caps);

public static class EntityRegistry
{
    private static readonly Capabilities CRUD = new(true, true, true, true, true);
    private static readonly Capabilities ReadOnly = new(true, true, false, false, false);
    private static readonly Capabilities ListCreate = new(true, true, true, false, false);

    // FK entity keys used below: "estimate-versions", "norms", "estimate-packages", "boq-items",
    // "cost-centres", "reporting-periods".
    public static readonly IReadOnlyList<EntityDescriptor> All = new EntityDescriptor[]
    {
        new("estimate-versions", "qs.estimate_versions", "Estimate Versions", new Column[]
        {
            new("version_no", ColKind.Int, Required: true, Updatable: false),
            new("effective_start", ColKind.Date),
            new("effective_end", ColKind.Date),
            new("source_hash", ColKind.Text),
            new("schedule_horizon_period_id", ColKind.Int),
        }, new[] { "version_no", "status" }, CRUD),   // status shown (read) but never written here — publish via workflow

        new("norms", "qs.norms", "Norms", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("norm_code", ColKind.Text, Required: true),
            new("description", ColKind.Text),
            new("unit", ColKind.Text),
            new("output_norm", ColKind.Numeric),
        }, new[] { "norm_code" }, CRUD),

        new("norm-materials", "qs.norm_materials", "Norm Materials", new Column[]
        {
            new("norm_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "norms"),
            new("material_code", ColKind.Text, Required: true),
            new("qty_per_unit", ColKind.Numeric),
        }, new[] { "material_code" }, CRUD),

        new("estimate-packages", "qs.estimate_packages", "Estimate Packages", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("code", ColKind.Text, Required: true),
            new("name", ColKind.Text),
        }, new[] { "code" }, CRUD),

        new("boq-items", "qs.boq_items", "BOQ Items", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("boq_sec", ColKind.Text, Required: true),
            new("item_ref", ColKind.Text, Required: true),
            new("description", ColKind.Text),
            new("unit", ColKind.Text),
            new("quantity", ColKind.Numeric),
            new("norm_id", ColKind.Bigint, FkEntity: "norms"),
            new("total_amount", ColKind.Numeric),
        }, new[] { "boq_sec", "item_ref" }, CRUD),

        new("boq-mappings", "qs.boq_norm_mappings", "BOQ → Norm Mappings", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("boq_item_id", ColKind.Bigint, Required: true, FkEntity: "boq-items"),
            new("norm_id", ColKind.Bigint, Required: true, FkEntity: "norms"),
            new("estimate_package_id", ColKind.Bigint, Required: true, FkEntity: "estimate-packages"),
        }, new[] { "boq_item_id" }, CRUD),

        new("resource-lines", "qs.estimate_resource_lines", "Estimate Resource Lines", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("boq_item_id", ColKind.Bigint, Required: true, FkEntity: "boq-items"),
            new("norm_id", ColKind.Bigint, FkEntity: "norms"),
            new("rtype", ColKind.Text, Required: true, Enum: new[] { "MANPOWER", "MATERIAL", "EQUIPMENT", "SUBCONTRACT" }),
            new("quantity", ColKind.Numeric),
            new("unit_rate_amount", ColKind.Numeric),
            // resource_cost_amount is generated → excluded
        }, new[] { "rtype" }, CRUD),

        new("cost-centres", "qs.cost_centres", "Cost Centres", new Column[]
        {
            new("bcc_id", ColKind.Text, Required: true),
            new("wbs_code", ColKind.Text),
            new("package_code", ColKind.Text),
            new("discipline", ColKind.Text),
            new("unit", ColKind.Text),
            new("estimate_package_id", ColKind.Bigint, FkEntity: "estimate-packages"),
            new("effective_start_period", ColKind.Int, Required: true),
            new("effective_end_period", ColKind.Int),
            new("planned_finish_period_id", ColKind.Int),
            new("is_plan_complete", ColKind.Bool),
        }, new[] { "bcc_id" }, CRUD),

        new("baselines", "qs.cost_centre_baselines", "Cost Centre Baselines", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("cost_centre_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "cost-centres"),
            new("bac_amount", ColKind.Numeric, Required: true),
            new("budget_qty", ColKind.Numeric),
        }, new[] { "cost_centre_id" }, CRUD),

        new("plan-periods", "qs.cost_centre_plan_periods", "Plan Curve (per period)", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("cost_centre_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "cost-centres"),
            new("reporting_period_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "reporting-periods"),
            new("planned_pct", ColKind.Numeric, Required: true),
        }, new[] { "cost_centre_id" }, CRUD),

        new("reporting-periods", "qs.reporting_periods", "Reporting Periods", new Column[]
        {
            new("period_id", ColKind.Int, Required: true, Updatable: false),
            new("period_start", ColKind.Date, Required: true),
            // status transitions via the open/close workflow, not generic CRUD
        }, new[] { "period_id", "status" }, CRUD),

        // ── read-only (procedure- or import-managed) ──
        new("cost-centre-periods", "qs.cost_centre_periods", "Cost Centre Periods (facts)", new Column[]
        {
            new("cost_centre_id", ColKind.Bigint, Insertable: false, Updatable: false),
            new("reporting_period_id", ColKind.Bigint, Insertable: false, Updatable: false),
            new("actual_pct_complete", ColKind.Numeric, Insertable: false, Updatable: false),
            new("lifecycle", ColKind.Text, Insertable: false, Updatable: false),
        }, new[] { "cost_centre_id", "reporting_period_id" }, ReadOnly),

        new("cost-deltas", "qs.period_cost_deltas", "Cost Ledger (deltas)", new Column[]
        {
            new("cost_centre_id", ColKind.Bigint, Insertable: false, Updatable: false),
            new("reporting_period_id", ColKind.Bigint, Insertable: false, Updatable: false),
            new("rtype", ColKind.Text, Insertable: false, Updatable: false),
            new("amount", ColKind.Numeric, Insertable: false, Updatable: false),
            new("direction", ColKind.Text, Insertable: false, Updatable: false),
        }, new[] { "idempotency_key" }, ReadOnly),   // create via the dedicated /capture/cost action

        new("import-runs", "qs.import_runs", "Import Runs", new Column[]
        {
            new("source_file", ColKind.Text, Insertable: false, Updatable: false),
            new("status", ColKind.Text, Insertable: false, Updatable: false),
            new("actor", ColKind.Text, Insertable: false, Updatable: false),
        }, new[] { "source_file" }, ReadOnly),
    };

    public static EntityDescriptor? Find(string key) => All.FirstOrDefault(e => e.Key == key);
}
