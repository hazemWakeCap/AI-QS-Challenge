namespace QsEarlyWarning.Infrastructure.Crud;

public enum ColKind { Text, Numeric, Int, Bigint, Bool, Date }

/// <summary>One editable/displayable column of a CRUD entity.</summary>
public sealed record Column(
    string Name, ColKind Kind, bool Insertable = true, bool Updatable = true,
    bool Required = false, string? FkEntity = null, string[]? Enum = null);

public sealed record Capabilities(bool List, bool Get, bool Create, bool Update, bool Delete);

/// <summary>Metadata for one CRUD-able table — the single source of truth for the API and the
/// auto-generated admin UI. Column names here are the ONLY names the generic service will put into
/// SQL (a whitelist). `id`/`project_id`/generated columns are handled by the service, not listed.
///
/// Workbook grouping (drives the Data-Admin "sheet" nav) is split into two tiers:
/// <list type="bullet">
/// <item><b>Group-level</b> — <c>Group</c>/<c>GroupLabel</c>/<c>GroupOrder</c> are shared by every
/// entity in a group and drive the primary nav; <c>Order</c> sorts tables within a group.
/// <c>GroupOrder</c> then <c>Order</c> give a total ordering (nav never depends on array position).</item>
/// <item><b>Entity-level lineage</b> — <c>SheetRef</c>/<c>Blurb</c> may differ per entity even inside
/// one group, so a group can mix imported and live-capture tables (e.g. the <c>periods</c> group's
/// <c>cost-deltas</c> is captured live and carries <c>SheetRef = null</c>, unlike its sheet-9-imported
/// group-mates). Keep this provenance-accurate — see the workbook mapping in the plan.</item>
/// </list></summary>
public sealed record EntityDescriptor(
    string Key, string Table, string Display, IReadOnlyList<Column> Columns,
    IReadOnlyList<string> NaturalKey, Capabilities Caps,
    string Group = "", string GroupLabel = "", int GroupOrder = 0,
    string? SheetRef = null, string Blurb = "", int Order = 0);

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
        }, new[] { "version_no", "status" }, CRUD,   // status shown (read) but never written here — publish via workflow
            Group: "system", GroupLabel: "System & Import", GroupOrder: 6, SheetRef: null, Order: 0,
            Blurb: "A versioned snapshot of the estimate — engine/audit, no source sheet."),

        new("norms", "qs.norms", "Norms", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("norm_code", ColKind.Text, Required: true),
            new("description", ColKind.Text),
            new("unit", ColKind.Text),
            new("output_norm", ColKind.Numeric),
        }, new[] { "norm_code" }, CRUD,
            Group: "norms", GroupLabel: "Estimate Norms", GroupOrder: 1, SheetRef: "2_ESTIMATE_NORMS", Order: 0,
            Blurb: "An estimating recipe: output + resource consumption per unit of work."),

        new("norm-materials", "qs.norm_materials", "Norm Materials", new Column[]
        {
            new("norm_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "norms"),
            new("material_code", ColKind.Text, Required: true),
            new("qty_per_unit", ColKind.Numeric),
        }, new[] { "material_code" }, CRUD,
            Group: "norms", GroupLabel: "Estimate Norms", GroupOrder: 1, SheetRef: "2_ESTIMATE_NORMS", Order: 1,
            Blurb: "Materials consumed per unit by a norm."),

        new("estimate-packages", "qs.estimate_packages", "Estimate Packages", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("code", ColKind.Text, Required: true),
            new("name", ColKind.Text),
        }, new[] { "code" }, CRUD,
            Group: "mapping", GroupLabel: "BOQ Mapping", GroupOrder: 2, SheetRef: "3_BOQ_MAPPING", Order: 1,
            Blurb: "An estimate package grouping BOQ lines for pricing."),

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
        }, new[] { "boq_sec", "item_ref" }, CRUD,
            Group: "boq", GroupLabel: "Bill of Quantities", GroupOrder: 0, SheetRef: "1_BOQ", Order: 0,
            Blurb: "One priced work item — a bill-of-quantities line."),

        new("boq-mappings", "qs.boq_norm_mappings", "BOQ → Norm Mappings", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("boq_item_id", ColKind.Bigint, Required: true, FkEntity: "boq-items"),
            new("norm_id", ColKind.Bigint, Required: true, FkEntity: "norms"),
            new("estimate_package_id", ColKind.Bigint, Required: true, FkEntity: "estimate-packages"),
        }, new[] { "boq_item_id" }, CRUD,
            Group: "mapping", GroupLabel: "BOQ Mapping", GroupOrder: 2, SheetRef: "3_BOQ_MAPPING", Order: 0,
            Blurb: "Links a BOQ line to its norm and estimate package."),

        new("resource-lines", "qs.estimate_resource_lines", "Estimate Resource Lines", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("boq_item_id", ColKind.Bigint, Required: true, FkEntity: "boq-items"),
            new("norm_id", ColKind.Bigint, FkEntity: "norms"),
            new("rtype", ColKind.Text, Required: true, Enum: new[] { "MANPOWER", "MATERIAL", "EQUIPMENT", "SUBCONTRACT" }),
            new("quantity", ColKind.Numeric),
            new("unit_rate_amount", ColKind.Numeric),
            // resource_cost_amount is generated → excluded
        }, new[] { "rtype" }, CRUD,
            Group: "datasheet", GroupLabel: "Estimate Datasheet", GroupOrder: 3, SheetRef: "4_ESTIMATE_DATASHEET", Order: 0,
            Blurb: "A BOQ item exploded into resource lines — unit rates live here."),

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
        }, new[] { "bcc_id" }, CRUD,
            Group: "cost-centres", GroupLabel: "Cost Centres & Budget", GroupOrder: 4, SheetRef: "9_HISTORICAL_DATA", Order: 0,
            Blurb: "A cost centre (WBS × package) tracked for progress and cost — imported from 9_HISTORICAL_DATA."),

        new("baselines", "qs.cost_centre_baselines", "Cost Centre Baselines", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("cost_centre_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "cost-centres"),
            new("bac_amount", ColKind.Numeric, Required: true),
            new("budget_qty", ColKind.Numeric),
        }, new[] { "cost_centre_id" }, CRUD,
            Group: "cost-centres", GroupLabel: "Cost Centres & Budget", GroupOrder: 4, SheetRef: "9_HISTORICAL_DATA", Order: 1,
            Blurb: "The budget-at-completion (BAC) baseline per cost centre — imported from 9_HISTORICAL_DATA."),

        new("plan-periods", "qs.cost_centre_plan_periods", "Plan Curve (per period)", new Column[]
        {
            new("estimate_version_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "estimate-versions"),
            new("cost_centre_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "cost-centres"),
            new("reporting_period_id", ColKind.Bigint, Required: true, Updatable: false, FkEntity: "reporting-periods"),
            new("planned_pct", ColKind.Numeric, Required: true),
        }, new[] { "cost_centre_id" }, CRUD,
            Group: "cost-centres", GroupLabel: "Cost Centres & Budget", GroupOrder: 4, SheetRef: "9_HISTORICAL_DATA", Order: 2,
            Blurb: "Planned % complete per period — the cost-centre S-curve, imported from 9_HISTORICAL_DATA."),

        new("reporting-periods", "qs.reporting_periods", "Reporting Periods", new Column[]
        {
            new("period_id", ColKind.Int, Required: true, Updatable: false),
            new("period_start", ColKind.Date, Required: true),
            // status transitions via the open/close workflow, not generic CRUD
        }, new[] { "period_id", "status" }, CRUD,
            Group: "periods", GroupLabel: "Periods & Actuals", GroupOrder: 5, SheetRef: "9_HISTORICAL_DATA", Order: 0,
            Blurb: "A monthly reporting period (open/close cycle) — imported from 9_HISTORICAL_DATA."),

        // ── read-only (procedure- or import-managed) ──
        new("cost-centre-periods", "qs.cost_centre_periods", "Cost Centre Periods (facts)", new Column[]
        {
            new("cost_centre_id", ColKind.Bigint, Insertable: false, Updatable: false),
            new("reporting_period_id", ColKind.Bigint, Insertable: false, Updatable: false),
            new("actual_pct_complete", ColKind.Numeric, Insertable: false, Updatable: false),
            new("lifecycle", ColKind.Text, Insertable: false, Updatable: false),
        }, new[] { "cost_centre_id", "reporting_period_id" }, ReadOnly,
            Group: "periods", GroupLabel: "Periods & Actuals", GroupOrder: 5, SheetRef: "9_HISTORICAL_DATA", Order: 1,
            Blurb: "Per-period actual progress facts for a cost centre — imported from 9_HISTORICAL_DATA."),

        new("cost-deltas", "qs.period_cost_deltas", "Cost Ledger (deltas)", new Column[]
        {
            new("cost_centre_id", ColKind.Bigint, Insertable: false, Updatable: false),
            new("reporting_period_id", ColKind.Bigint, Insertable: false, Updatable: false),
            new("rtype", ColKind.Text, Insertable: false, Updatable: false),
            new("amount", ColKind.Numeric, Insertable: false, Updatable: false),
            new("direction", ColKind.Text, Insertable: false, Updatable: false),
        }, new[] { "idempotency_key" }, ReadOnly,   // create via the dedicated /capture/cost action
            Group: "periods", GroupLabel: "Periods & Actuals", GroupOrder: 5, SheetRef: null, Order: 2,
            Blurb: "Per-period cost movements posted to the ledger — captured live via /capture/cost, not imported."),

        new("import-runs", "qs.import_runs", "Import Runs", new Column[]
        {
            new("source_file", ColKind.Text, Insertable: false, Updatable: false),
            new("status", ColKind.Text, Insertable: false, Updatable: false),
            new("actor", ColKind.Text, Insertable: false, Updatable: false),
        }, new[] { "source_file" }, ReadOnly,
            Group: "system", GroupLabel: "System & Import", GroupOrder: 6, SheetRef: null, Order: 1,
            Blurb: "An audit record of each workbook import — engine/audit, no source sheet."),
    };

    public static EntityDescriptor? Find(string key) => All.FirstOrDefault(e => e.Key == key);
}
