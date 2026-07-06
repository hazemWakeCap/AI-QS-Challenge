using Microsoft.AspNetCore.Mvc;
using QsEarlyWarning.Core;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Web.API.Contracts;

namespace QsEarlyWarning.Web.API.Controllers;

[ApiController]
[Route("api/v1/watchlist")]
public sealed class WatchlistController : ControllerBase
{
    private readonly IModelProvider _provider;
    private readonly WatchlistScoringService _scoring;

    public WatchlistController(IModelProvider provider, WatchlistScoringService scoring)
    {
        _provider = provider;
        _scoring = scoring;
    }

    /// <summary>
    /// Ranked GREEN-centres-about-to-tip for a period. Plan §6.9.
    /// 400 = malformed/out-of-range input; 404 = valid period with no matching artifact.
    /// </summary>
    [HttpGet]
    public ActionResult<WatchlistResponseDto> Get([FromQuery] int period, [FromQuery] int k = 10)
    {
        if (period < 1 || period > EvmThresholds.ForecastPeriod)
            return BadRequest($"period must be in [1, {EvmThresholds.ForecastPeriod}].");
        if (k is not (5 or 10))
            return BadRequest("k must be 5 or 10.");

        var snapshot = _provider.Current;
        var result = _scoring.ScorePeriod(snapshot.Panel, period, snapshot.Model);
        if (result.Status == ScoreStatus.NoArtifact)
            return NotFound($"No model artifact serves period {period} (retrospective range is " +
                            $"{EvmThresholds.MinTrainOrigin}..{EvmThresholds.LastLabeledPeriod}; forecast is " +
                            $"{EvmThresholds.ForecastPeriod}).");

        var rows = result.Rows
            .Take(k)
            .Select((r, i) => new WatchlistRowDto(
                i + 1, r.BccId, r.Discipline, r.PackageCode, r.RiskScore, r.Cpi, r.Gap, r.RiskIndicators))
            .ToList();

        return Ok(new WatchlistResponseDto(
            period, k, result.IsForecast, result.ArtifactVersion!, result.TrainingCutoffPeriod,
            result.Rows.Count, rows));
    }
}
