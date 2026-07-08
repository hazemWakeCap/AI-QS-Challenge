using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Forecasting;
using QsEarlyWarning.Core.Registry;
using QsEarlyWarning.Core.StressTest;
using QsEarlyWarning.Infrastructure.Excel;

namespace QsEarlyWarning.Tests;

/// <summary>
/// Builds a <see cref="ProjectSnapshot"/> from the Excel workbook, mirroring
/// <c>ProjectSnapshotRegistry.Build</c> (train model → fit forecaster → run stress test), so the
/// copilot tools and the idea-4 eval harness can run without a live Postgres.
/// </summary>
public static class TestSnapshot
{
    public const long OwningId = 42;

    public static ProjectSnapshot Build()
    {
        var panel = new ExcelPanelLoader().Load(TestData.WorkbookPath);
        var model = new RollingOriginEvaluator().Train(panel);
        var periods = panel.Select(p => p.PeriodId).ToList();

        IncrementalSpendForecaster? forecaster = null;
        ForecastValidationSummary? backtest = null;
        try
        {
            var f = new IncrementalSpendForecaster();
            f.Fit(panel, model.Origins);
            backtest = new ForecastEvaluator().Evaluate(panel, model.Origins);
            forecaster = f;
        }
        catch { /* forecaster unavailable */ }

        StressTestReport? stress = null;
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>? mix = null;
        try
        {
            var est = new EstimateWorkbookLoader(TestData.WorkbookPath, OwningId).TryLoadForProject(OwningId);
            if (est is not null)
            {
                stress = new EstimateStressTester().Run(est, panel);
                mix = BuildResourceMix(est);
            }
        }
        catch { /* stress test / mix unavailable */ }

        return new ProjectSnapshot
        {
            ProjectId = OwningId,
            BuiltForUserId = 1,
            Panel = panel,
            Model = model,
            MinPeriod = periods.Min(),
            ForecastPeriod = periods.Max(),
            BuiltAtUtc = DateTimeOffset.UtcNow,
            Forecaster = forecaster,
            ForecastBacktest = backtest,
            StressTest = stress,
            ResourceMix = mix,
        };
    }

    private static readonly HashSet<string> Canonical =
        new(StringComparer.OrdinalIgnoreCase) { "MANPOWER", "MATERIAL", "EQUIPMENT", "SUBCONTRACT" };

    // Mirrors ProjectSnapshotRegistry.BuildResourceMix so the eval runs without Postgres.
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> BuildResourceMix(
        QsEarlyWarning.Domain.Estimate.EstimateModel est)
    {
        var byPkg = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var line in est.ResourceLines)
        {
            if (line.Package is not string pkg || !pkg.StartsWith("EP-", StringComparison.OrdinalIgnoreCase)) continue;
            var type = line.ResourceType?.Trim().ToUpperInvariant();
            if (type is null || !Canonical.Contains(type)) continue;
            if (line.ResourceCost is not double cost || !double.IsFinite(cost)) continue;
            if (!byPkg.TryGetValue(pkg, out var m)) byPkg[pkg] = m = new(StringComparer.OrdinalIgnoreCase);
            m[type] = m.GetValueOrDefault(type) + cost;
        }
        return byPkg.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double>)kv.Value, StringComparer.Ordinal);
    }
}
