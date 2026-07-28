using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core;
using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Web.API.Contracts;

namespace QsEarlyWarning.Web.API.Controllers;

[ApiController]
[Route("api/v1/validation-summary")]
public sealed class ValidationSummaryController : ControllerBase
{
    private readonly IModelProvider _provider;

    public ValidationSummaryController(IModelProvider provider) => _provider = provider;

    /// <summary>
    /// The frozen out-of-fold validation report — MODEL-LEVEL, not the selected period's live
    /// accuracy (plan §6.9). Historical backtest of the one deployed rule scorer.
    /// </summary>
    [HttpGet]
    public ActionResult<ValidationSummaryDto> Get()
    {
        var s = _provider.Current.Model.Summary;

        return Ok(new ValidationSummaryDto(
            Provenance: "Historical backtest (rolling-origin, out-of-fold). Model-level; not the selected " +
                        "period's live accuracy. Exploratory single-project evidence — organiser-generated " +
                        "workbook, no cross-project claim.",
            Scorer: s.Scorer,
            ScorerVersion: s.ScorerVersion,
            FeatureSchemaVersion: s.FeatureSchemaVersion,
            EvaluationOriginMin: s.EvaluationOriginMin,
            EvaluationOriginMax: s.EvaluationOriginMax,
            FoldCount: s.FoldCount,
            TotalTransitions: s.TotalTransitions,
            Rule: s.Rule.Select(Map).ToList(),
            CpiNative: s.CpiNative.Select(Map).ToList(),
            Challenger: s.Challenger?.Select(Map).ToList(),
            Collinearity: MapCollinearity(_provider.Current.Panel),
            DecisionsPerScorer: s.FoldCount * Domain.Constants.EvmThresholds.SelectionK));
    }

    /// <summary>
    /// Measured from the panel on every request rather than cached, so the published verdict can
    /// never drift away from the data actually loaded.
    /// </summary>
    private static CollinearityDto? MapCollinearity(IReadOnlyList<Domain.Entities.CostCentrePeriod> panel)
    {
        var c = ZoneDisciplineCollinearity.Measure(panel);
        if (c.ZoneCount == 0) return null;   // workbook carries no zone column

        return new CollinearityDto(
            c.ZoneCount, c.DisciplineCount, c.SingleDisciplineZones, c.DisciplinesSpanningZones,
            c.ZoneIsProxyForDiscipline, c.MostMixedZone, c.MostMixedZoneDisciplines, c.Verdict,
            c.Zones.Select(z => new ZoneCompositionDto(
                z.ZoneArea, z.CentreCount, z.DisciplineCount, z.Disciplines)).ToList());
    }

    private static ScorerReportDto Map(ScorerReport r) => new(
        r.ScorerLabel, r.K, r.MacroPrecision, r.MacroRecall, r.PrecisionMin, r.PrecisionMax,
        r.FalseAlertsPerCycle,
        r.Folds.Select(f => new FoldMetricDto(
            f.PeriodId, f.K, f.KEffective, f.Eligible, f.Positives,
            f.TruePositives, f.FalsePositives, f.FalseNegatives, f.Precision, f.Recall)).ToList());
}
