using QsEarlyWarning.Agent;
using QsEarlyWarning.Core;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Infrastructure.Excel;
using QsEarlyWarning.Infrastructure.Postgres;
using QsEarlyWarning.Web.API.Tenancy;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicy = "qs-frontend";

builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
    p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin())); // demo: React dev host

// Resolve the workbook path: config override, else the repo's data/ folder (walk up from cwd).
var workbookPath = builder.Configuration["Data:WorkbookPath"] ?? LocateWorkbook();

builder.Services.AddSingleton<IPanelLoader, ExcelPanelLoader>();
builder.Services.AddSingleton<WatchlistScoringService>();
// Load + train once at startup (fails loud if the workbook is invalid). Still backs the copilot /
// health while those move to live data in a later phase.
builder.Services.AddSingleton<IModelProvider>(sp =>
    new ModelProvider(sp.GetRequiredService<IPanelLoader>(), workbookPath));

// Phase 2/2b/2c: serve the watchlist from Postgres behind RLS via the project-aware registry.
var connString = builder.Configuration.GetConnectionString("Qs")
    ?? $"Host=localhost;Port=5432;Database=qs_phase1;Username={Environment.UserName}";
builder.Services.AddSingleton<IProjectPanelSource>(_ => new PostgresPanelLoader(connString));
builder.Services.AddSingleton<IProjectSnapshotRegistry>(sp =>
    new ProjectSnapshotRegistry(sp.GetRequiredService<IProjectPanelSource>()));
builder.Services.AddSingleton(new ProjectResolver(connString));
builder.Services.AddScoped<TenantContext>();

// S2 copilot: Microsoft Agent Framework over Claude, or a disabled agent when no key is set.
builder.Services.AddQsCostCopilot(builder.Configuration);

var app = builder.Build();

app.UseCors(CorsPolicy);
app.UseMiddleware<TenantContextMiddleware>();
app.MapControllers();

// Warm the Excel model at boot (copilot/health). The Postgres registry builds lazily per project.
_ = app.Services.GetRequiredService<IModelProvider>().Current;

app.Run();

static string LocateWorkbook()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "data", "Tower_X_Project_Data.xlsx");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    throw new FileNotFoundException(
        "Tower_X_Project_Data.xlsx not found. Set Data:WorkbookPath in config or run from the repo.");
}

public partial class Program { } // for potential WebApplicationFactory tests
