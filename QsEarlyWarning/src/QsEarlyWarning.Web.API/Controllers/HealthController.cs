using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core;
using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Web.API.Contracts;

namespace QsEarlyWarning.Web.API.Controllers;

[ApiController]
[Route("api/v1/health")]
public sealed class HealthController : ControllerBase
{
    private readonly IModelProvider _provider;

    public HealthController(IModelProvider provider) => _provider = provider;

    /// <summary>Read-only load state (plan §6.9). Never mutates.</summary>
    [HttpGet]
    public ActionResult<HealthDto> Get()
    {
        var s = _provider.Current;
        return Ok(new HealthDto(
            Status: "ok",
            Workbook: Path.GetFileName(s.WorkbookPath),
            RowCount: s.RowCount,
            CentreCount: s.CentreCount,
            ScorerVersion: RuleArtifact.ScorerVersion,
            FeatureSchemaVersion: RollingOriginEvaluator.FeatureSchemaVersion,
            ForecastPeriod: EvmThresholds.ForecastPeriod));
    }
}
