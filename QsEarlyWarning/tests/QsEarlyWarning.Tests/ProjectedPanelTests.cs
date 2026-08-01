using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Forecasting;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// The projected EVM panel — the two forecasters composed into a cost-centre row at a period the
/// workbook does not reach.
///
/// The tests that matter most here are the ones about what the projection is NOT allowed to do. It may
/// not touch reported history (<see cref="Measured_periods_are_a_passthrough_of_the_reported_rows"/>),
/// it may not invent a planned value where the baseline curve has ended, and it may not produce an AC
/// for a centre the spend forecaster cannot speak to. EV is derived from projected progress, and that
/// is licensed by the schema's own definition of earned value — the identity is pinned in
/// <see cref="Ev_is_bac_times_projected_percent_complete"/> so a refactor cannot quietly replace it
/// with a second, unvalidated cost model.
/// </summary>
public sealed class ProjectedPanelTests
{
    private static readonly CostCentrePeriod[] Panel = new ExcelPanelLoader().Load(TestData.WorkbookPath).ToArray();
    private static readonly ReportingOrigins Origins = ReportingOrigins.FromPanel(Panel);
    private static readonly ProgressValidationSummary Validation = new ProgressBacktest().Evaluate(Panel, Origins);

    private static readonly EvmProjector Projector = Build();

    private static EvmProjector Build()
    {
        var spend = new IncrementalSpendForecaster();
        spend.Fit(Panel, Origins);
        var progress = new ProgressForecaster(Panel, Origins.ForecastPeriod, Validation);
        return new EvmProjector(Panel, progress, spend, Origins.ForecastPeriod);
    }

    private static int Origin => Origins.ForecastPeriod;

    // ── the passthrough: reported history must survive the projection untouched ──

    [Fact]
    public void Measured_periods_are_a_passthrough_of_the_reported_rows()
    {
        for (int p = Projector.MinPeriod; p <= Origin; p++)
        {
            var projected = Projector.ProjectAt(p).Centres;
            var reported = Panel.Where(r => r.PeriodId == p)
                .OrderBy(r => r.BccId, StringComparer.Ordinal).ToList();

            Assert.Equal(reported.Count, projected.Count);
            for (int i = 0; i < reported.Count; i++)
            {
                Assert.Equal(reported[i].BccId, projected[i].BccId);
                Assert.Equal(ProjectionBasis.Measured, projected[i].Basis);
                Assert.Equal(reported[i].BacAed ?? 0, projected[i].Bac, 4);
                Assert.Equal(reported[i].EvAed ?? 0, projected[i].Ev, 4);
                Assert.Equal(reported[i].AcCumulative, projected[i].Ac);
                Assert.Equal(reported[i].Cpi, projected[i].Cpi);
                Assert.Equal(reported[i].Spi, projected[i].Spi);
                Assert.Equal(reported[i].PvAed, projected[i].Pv);
                Assert.Equal(reported[i].AlertLevel ?? "GREEN", projected[i].AlertLevel);
                Assert.False(projected[i].AlertProjected);
            }
        }
    }

    // ── the one derivation, pinned ──

    [Fact]
    public void Ev_is_bac_times_projected_percent_complete()
    {
        foreach (var period in new[] { Origin + 1, Origin + 3, Origin + 6 })
            foreach (var row in Projector.ProjectAt(period).Centres)
                Assert.Equal(Math.Round(row.Bac * row.PctComplete / 100.0, 2), row.Ev, 2);
    }

    [Fact]
    public void Earned_value_never_exceeds_budget_and_reaches_it_exactly_at_the_projected_finish()
    {
        var finishing = Projector.ProjectAt(Origin + 1).Centres
            .Where(r => r.ProjectedFinishPeriod is int f && f > Origin && f <= Projector.MaxPeriod)
            .Take(20).ToList();
        Assert.NotEmpty(finishing);

        foreach (var centre in finishing)
        {
            var atFinish = Projector.ProjectAt(centre.ProjectedFinishPeriod!.Value, new[] { centre.BccId })
                .Centres.Single();
            Assert.Equal(100.0, atFinish.PctComplete, 4);
            Assert.Equal(atFinish.Bac, atFinish.Ev, 2);
        }

        for (int p = Origin + 1; p <= Origin + 8; p++)
            Assert.All(Projector.ProjectAt(p).Centres, r => Assert.True(r.Ev <= r.Bac + 0.01));
    }

    // ── shape over time ──

    [Fact]
    public void Earned_value_and_actual_cost_never_move_backwards()
    {
        var byCentre = new Dictionary<string, (double Ev, double Ac, double Lo, double Hi)>();

        for (int p = Origin + 1; p <= Origin + 8; p++)
            foreach (var row in Projector.ProjectAt(p).Centres)
            {
                if (byCentre.TryGetValue(row.BccId, out var prev))
                {
                    Assert.True(row.Ev >= prev.Ev - 0.01, $"{row.BccId} EV fell at period {p}");
                    if (row.Ac is double ac)
                    {
                        Assert.True(ac >= prev.Ac - 0.01, $"{row.BccId} AC fell at period {p}");
                        Assert.True(row.AcP10 is not double lo || lo >= prev.Lo - 0.01);
                        Assert.True(row.AcP90 is not double hi || hi >= prev.Hi - 0.01);
                    }
                }
                byCentre[row.BccId] = (row.Ev, row.Ac ?? 0, row.AcP10 ?? 0, row.AcP90 ?? 0);
            }
    }

    [Fact]
    public void Actual_cost_band_brackets_the_median()
    {
        foreach (var row in Projector.ProjectAt(Origin + 2).Centres.Where(r => r.AcAvailable))
        {
            if (row.AcP10 is double lo) Assert.True(lo <= row.Ac!.Value + 0.01);
            if (row.AcP90 is double hi) Assert.True(hi >= row.Ac!.Value - 0.01);
        }
    }

    [Fact]
    public void Spend_is_frozen_past_a_centre_projected_finish()
    {
        var centre = Projector.ProjectAt(Origin + 1).Centres
            .First(r => r.AcAvailable && r.ProjectedFinishPeriod is int f
                        && f > Origin && f + 2 <= Projector.MaxPeriod);
        int finish = centre.ProjectedFinishPeriod!.Value;

        var at = Projector.ProjectAt(finish, new[] { centre.BccId }).Centres.Single();
        var after = Projector.ProjectAt(finish + 2, new[] { centre.BccId }).Centres.Single();

        Assert.Equal(at.Ac!.Value, after.Ac!.Value, 2);
        Assert.Equal(at.Cpi!.Value, after.Cpi!.Value, 6);
        Assert.Contains("finish", after.AcNote!, StringComparison.OrdinalIgnoreCase);
    }

    // ── provenance ──

    [Fact]
    public void Basis_degrades_from_measured_through_forecast_to_extrapolated()
    {
        Assert.Equal(ProjectionBasis.Measured, Projector.ProjectAt(Origin).Basis);

        var near = Projector.ProjectAt(Origin + 2);
        Assert.Equal(ProjectionBasis.Forecast, near.Basis);
        Assert.All(near.Centres, r => Assert.NotEqual(ProjectionBasis.Measured, r.Basis));

        // Past the spend back-test the cost leg is on a held run-rate, so the row cannot claim more.
        var far = Projector.ProjectAt(Projector.SpendBacktestedThroughPeriod + 1);
        Assert.Equal(ProjectionBasis.Extrapolated, far.Basis);
        Assert.All(far.Centres.Where(r => r.AcAvailable),
            r => Assert.Equal(ProjectionBasis.Extrapolated, r.Basis));
    }

    [Fact]
    public void Planned_value_and_spi_are_null_past_the_origin_and_the_reason_is_stated()
    {
        var measured = Projector.ProjectAt(Origin);
        Assert.True(measured.PvAvailable);

        var projected = Projector.ProjectAt(Origin + 1);
        Assert.False(projected.PvAvailable);
        Assert.Contains($"period {Origin}", projected.PvReason!);
        Assert.All(projected.Centres, r =>
        {
            Assert.Null(r.Pv);
            Assert.Null(r.Spi);
            Assert.Null(r.PlannedPct);
        });
    }

    [Fact]
    public void Every_projected_response_states_its_assumptions()
    {
        var notes = Projector.ProjectAt(Origin + 5).Notes;
        Assert.Contains(notes, n => n.Contains("schema", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notes, n => n.Contains("cost performance observed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notes, n => n.Contains("stop spending", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Past_the_spend_backtest_cost_performance_is_carried_and_lands_on_the_directional_eac()
    {
        // The check that ties the whole composition together. Past the cone the remaining work is
        // priced at the CPI the cone ends on, so CPI holds flat and EAC stops moving — and at the
        // centre's projected finish, where EV has reached BAC, cumulative AC must equal that EAC
        // exactly. If those two ever diverge, the cost-to-complete and the forecast final cost are no
        // longer the same statement and one of them is wrong.
        var candidates = Projector.ProjectAt(Origin + 1).Centres
            .Where(r => r.AcAvailable && r.ProjectedFinishPeriod is int f
                        && f > Projector.SpendBacktestedThroughPeriod && f <= Projector.MaxPeriod)
            .Take(15).ToList();
        Assert.NotEmpty(candidates);

        foreach (var c in candidates)
        {
            int finish = c.ProjectedFinishPeriod!.Value;
            var atCone = Projector.ProjectAt(Projector.SpendBacktestedThroughPeriod, new[] { c.BccId }).Centres.Single();
            var beyond = Projector.ProjectAt(finish - 1, new[] { c.BccId }).Centres.Single();
            var atFinish = Projector.ProjectAt(finish, new[] { c.BccId }).Centres.Single();

            if (atCone.Cpi is not double cpi || cpi <= 0) continue;

            Assert.Equal(cpi, beyond.Cpi!.Value, 3);              // performance carried, not re-forecast
            Assert.Equal(atCone.Eac!.Value, beyond.Eac!.Value, 0);
            Assert.True(atFinish.Ac!.Value < beyond.Ac!.Value + 0.01 + atFinish.Bac);
            Assert.Equal(atFinish.Eac!.Value, atFinish.Ac!.Value, 0);   // EV = BAC there, so EAC = AC
        }
    }

    // ── the refusals ──

    [Fact]
    public void Without_a_spend_forecast_the_earned_value_still_stands_and_the_rest_is_refused()
    {
        // The two engines degrade independently, so a project that fits progress but not spend must
        // still get its EV — the figure that comes from the progress projection — and nothing else.
        var progressOnly = new EvmProjector(
            Panel, new ProgressForecaster(Panel, Origin, Validation), null, Origin);

        var rows = progressOnly.ProjectAt(Origin + 2).Centres;
        Assert.NotEmpty(rows);

        foreach (var r in rows)
        {
            Assert.False(r.AcAvailable);
            Assert.Null(r.Ac);
            Assert.Null(r.Cpi);
            Assert.Null(r.Eac);
            Assert.Null(r.Vac);
            Assert.False(r.AlertProjected);
            Assert.False(string.IsNullOrWhiteSpace(r.AcNote));
            Assert.Equal(Math.Round(r.Bac * r.PctComplete / 100.0, 2), r.Ev, 2);   // EV still stands
        }
    }

    [Fact]
    public void A_centre_the_spend_model_is_unsure_of_carries_the_doubt_rather_than_hiding_the_figure()
    {
        // Matching how /forecast/cost-centres serves its trust badge: a centre below the progress gate
        // still gets a cone, and the row says the interval was not calibrated that early.
        var early = Projector.ProjectAt(Origin + 2).Centres
            .Where(r => r.AcAvailable && r.AcNote is not null
                        && r.AcNote.Contains("progress gate", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(early);
        Assert.All(early, r => Assert.NotNull(r.Ac));
    }

    [Fact]
    public void A_stalled_centre_holds_its_percentage_and_claims_no_finish()
    {
        var stalled = Projector.ProjectAt(Origin + 4).Centres.Where(r => r.Stalled).ToList();
        Assert.NotEmpty(stalled);

        foreach (var r in stalled)
        {
            Assert.Null(r.ProjectedFinishPeriod);
            Assert.Equal(0, r.PacePctPerPeriod, 6);
            var atOrigin = Panel.Single(p => p.PeriodId == Origin && p.BccId == r.BccId);
            Assert.Equal(atOrigin.ActualPctComplete ?? 0, r.PctComplete, 4);
        }
    }

    [Fact]
    public void Periods_outside_the_servable_range_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Projector.ProjectAt(Projector.MinPeriod - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Projector.ProjectAt(Projector.MaxPeriod + 1));
        Assert.Equal(Origin + ProgressConfig.MaxHorizon, Projector.MaxPeriod);
    }

    [Fact]
    public void Without_a_progress_forecaster_nothing_past_the_origin_can_be_served()
    {
        var spend = new IncrementalSpendForecaster();
        spend.Fit(Panel, Origins);
        var bare = new EvmProjector(Panel, null, spend, Origin);

        Assert.False(bare.CanProject);
        Assert.NotEmpty(bare.ProjectAt(Origin).Centres);                      // reported history still serves
        Assert.Throws<InvalidOperationException>(() => bare.ProjectAt(Origin + 1));
    }

    // ── the EVM identities, matching the SQL view ──

    [Fact]
    public void Cost_variance_and_eac_follow_the_same_formulas_as_the_evm_view()
    {
        foreach (var r in Projector.ProjectAt(Origin + 3).Centres.Where(r => r.AcAvailable))
        {
            Assert.Equal(Math.Round(r.Ev - r.Ac!.Value, 2), r.Cv!.Value, 2);

            // The view divides by AC and NULLIFs only that denominator, so a centre with no earned
            // value reads CPI 0 rather than null — and only a centre with no spend at all reads null.
            Assert.Equal(r.Ac.Value > 0 ? r.Ev / r.Ac.Value : (double?)null, r.Cpi);

            if (r.Ev > 0)
                Assert.Equal(Math.Round(r.Bac * r.Ac.Value / r.Ev, 2), r.Eac!.Value, 2);
            else
                Assert.Equal(r.Bac, r.Eac!.Value, 2);     // the view's CASE: EAC falls back to BAC

            Assert.Equal(Math.Round(r.Bac - r.Eac!.Value, 2), r.Vac!.Value, 2);
        }
    }

    [Fact]
    public void The_projected_alert_is_recomputed_from_the_projected_cpi_and_marked_as_projected()
    {
        var rows = Projector.ProjectAt(Origin + 2).Centres
            .Where(r => r.AlertProjected && r.Lifecycle == "IN_PROGRESS").ToList();
        Assert.NotEmpty(rows);

        foreach (var r in rows)
            Assert.Equal(r.Cpi!.Value < 0.95 ? "AMBER" : "GREEN", r.AlertLevel);

        // A closed or not-started centre keeps its reported verdict — there is no CPI to recompute from.
        Assert.All(Projector.ProjectAt(Origin + 2).Centres.Where(r => r.Lifecycle != "IN_PROGRESS"),
            r => Assert.False(r.AlertProjected));
    }
}
