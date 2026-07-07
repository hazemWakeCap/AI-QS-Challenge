using QsEarlyWarning.Agent;
using QsEarlyWarning.Core;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Infrastructure.Excel;

var builder = WebApplication.CreateBuilder(args);

const string CorsPolicy = "qs-frontend";

builder.Services.AddControllers();
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p =>
    p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin())); // demo: React dev host

// Resolve the workbook path: config override, else the repo's data/ folder (walk up from cwd).
var workbookPath = builder.Configuration["Data:WorkbookPath"] ?? LocateWorkbook();

builder.Services.AddSingleton<IPanelLoader, ExcelPanelLoader>();
builder.Services.AddSingleton<WatchlistScoringService>();
// Load + train once at startup (fails loud if the workbook is invalid).
builder.Services.AddSingleton<IModelProvider>(sp =>
    new ModelProvider(sp.GetRequiredService<IPanelLoader>(), workbookPath));

// S2 copilot: Microsoft Agent Framework over Claude, or a disabled agent when no key is set.
builder.Services.AddQsCostCopilot(builder.Configuration);

var app = builder.Build();

app.UseCors(CorsPolicy);
app.MapControllers();

// Warm the model at boot so the first request is fast and a bad workbook fails immediately.
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
