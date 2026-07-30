using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Forecasting;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;

namespace QsEarlyWarning.Tests;

/// <summary>
/// The physical-progress projection that lets the 4D build sequence run past the last reported period.
///
/// Two kinds of test here. The synthetic ones pin the arithmetic and the refusals — clamping, stalled
/// centres, band ordering, the horizon cap. The workbook ones pin the *claim*: the projection is only
/// allowed on screen because it was measured, so a refactor that quietly degrades its accuracy has to
/// fail here rather than ship a wider error behind the same label.
/// </summary>
public sealed class ProgressForecastTests
{
    private static readonly CostCentrePeriod[] Panel = new ExcelPanelLoader().Load(TestData.WorkbookPath).ToArray();
    private static readonly ReportingOrigins Origins = ReportingOrigins.FromPanel(Panel);
    private static readonly ProgressValidationSummary Validation = new ProgressBacktest().Evaluate(Panel, Origins);

    /// <summary>A single centre whose reported progress follows the given percentages from period 1.</summary>
    private static CostCentrePeriod[] Synthetic(string bccId, params double?[] actualPct) =>
        actualPct.Select((pct, i) => new CostCentrePeriod
        {
            PeriodId = i + 1,
            BccId = bccId,
            PackageCode = "EP-TEST",
            ActualPctComplete = pct,
            AlertLevel = "AMBER",
        }).ToArray();

    private static ProgressForecaster ForecasterFor(CostCentrePeriod[] panel, int origin) =>
        new(panel, origin, Validation);

    // ── the arithmetic ──

    [Fact]
    public void Pace_is_the_mean_of_the_last_three_reported_increments()
    {
        // Increments 10, 6, 2 over periods 2..4 → mean 6 pp per period.
        var panel = Synthetic("BCC-A", 0, 10, 16, 18);
        var byPeriod = panel.ToDictionary(p => p.PeriodId);

        Assert.Equal(6.0, IncrementHelper.RecentProgressPace(byPeriod, 4)!.Value, 6);

        var c = ForecasterFor(panel, 4).Project(new[] { "BCC-A" }, 6).Centres.Single();
        Assert.Equal(6.0, c.PacePctPerPeriod, 4);
        Assert.Equal(24.0, c.Points.Single(p => p.Period == 5).P50Pct, 4);
        Assert.Equal(30.0, c.Points.Single(p => p.Period == 6).P50Pct, 4);
    }

    [Fact]
    public void Projection_is_capped_at_one_hundred_percent()
    {
        var panel = Synthetic("BCC-A", 60, 75, 88, 96);   // pace 12 → would read 156% by period 9
        var c = ForecasterFor(panel, 4).Project(new[] { "BCC-A" }, 9).Centres.Single();

        Assert.All(c.Points, p => Assert.InRange(p.P50Pct, 0, 100));
        Assert.All(c.Points, p => Assert.InRange(p.P90Pct!.Value, 0, 100));
        Assert.Equal(100, c.Points.Single(p => p.Period == 9).P50Pct, 4);
    }

    [Fact]
    public void Finish_period_is_the_first_period_the_centre_reads_one_hundred()
    {
        var panel = Synthetic("BCC-A", 0, 10, 20, 30);    // pace 10, 70 pp remaining → 7 more periods
        var c = ForecasterFor(panel, 4).Project(new[] { "BCC-A" }, null).Centres.Single();

        Assert.Equal(11, c.ProjectedFinishPeriod);
        Assert.False(c.Stalled);
    }

    // ── the refusals ──

    [Fact]
    public void A_centre_with_no_pace_is_stalled_and_has_no_finish_period()
    {
        // Flat for three periods: nothing has been booked, so there is no pace to carry forward.
        var panel = Synthetic("BCC-A", 4, 4, 4, 4);
        var c = ForecasterFor(panel, 4).Project(new[] { "BCC-A" }, 8).Centres.Single();

        Assert.True(c.Stalled);
        Assert.Null(c.ProjectedFinishPeriod);
        Assert.Equal(0, c.PacePctPerPeriod);
        // It stays at its reported percentage rather than creeping upward on an invented pace.
        Assert.All(c.Points.Where(p => p.Tier != ProgressTier.Measured), p => Assert.Equal(4, p.P50Pct, 4));
    }

    [Fact]
    public void Negative_reported_progress_never_un_builds_the_model()
    {
        // A re-measurement that walks progress backwards. Projecting it forward would delete work
        // that physically exists, so the pace is clamped at zero and the centre reads stalled.
        var panel = Synthetic("BCC-A", 40, 38, 35, 30);
        var c = ForecasterFor(panel, 4).Project(new[] { "BCC-A" }, 8).Centres.Single();

        Assert.True(c.Stalled);
        Assert.Equal(0, c.PacePctPerPeriod);
        Assert.All(c.Points, p => Assert.True(p.P50Pct >= 0));
    }

    [Fact]
    public void Horizon_is_capped_so_a_creeping_centre_cannot_request_an_unbounded_timeline()
    {
        // 0.5 pp per period from 10% needs 180 periods to finish — far past anything defensible.
        var panel = Synthetic("BCC-A", 8.5, 9.0, 9.5, 10.0);
        var f = ForecasterFor(panel, 4).Project(new[] { "BCC-A" }, through: 500);

        Assert.Equal(4 + ProgressConfig.MaxHorizon, f.HorizonPeriod);
        // No finish inside the cap means no finish is claimed at all.
        Assert.Null(f.Centres.Single().ProjectedFinishPeriod);
        Assert.Equal(4, f.SuggestedHorizonPeriod);
    }

    // ── the bands ──

    [Fact]
    public void Bands_are_ordered_at_every_point()
    {
        var f = ForecasterFor(Panel, Origins.ForecastPeriod).Project(null, null);

        foreach (var c in f.Centres)
            foreach (var p in c.Points)
            {
                Assert.True(p.P10Pct <= p.P50Pct, $"{c.BccId} P{p.Period}: P10 {p.P10Pct} > P50 {p.P50Pct}");
                Assert.True(p.P50Pct <= p.P90Pct, $"{c.BccId} P{p.Period}: P50 {p.P50Pct} > P90 {p.P90Pct}");
            }
    }

    [Fact]
    public void Projected_points_never_move_backwards()
    {
        // Monotonicity is a property of the PROJECTION, not of reported history. The workbook contains
        // one genuine reversal — BCC-COM-SEC-1216 reports 0.83% at period 10 and 0% at period 11, a
        // re-baselining — and the measured series is entitled to show it. What must never happen is a
        // projection walking a building backwards, which is what this asserts.
        var f = ForecasterFor(Panel, Origins.ForecastPeriod).Project(null, null);

        foreach (var c in f.Centres)
        {
            var projected = c.Points.Where(p => p.Tier != ProgressTier.Measured).ToList();
            double prevLow = c.ActualPctAtOrigin, prevHigh = c.ActualPctAtOrigin, prevP50 = c.ActualPctAtOrigin;

            foreach (var p in projected)
            {
                Assert.True(p.P50Pct >= prevP50 - 1e-9, $"{c.BccId} P{p.Period}: P50 went backwards");
                Assert.True(p.P10Pct >= prevLow - 1e-9, $"{c.BccId} P{p.Period}: P10 went backwards");
                Assert.True(p.P90Pct >= prevHigh - 1e-9, $"{c.BccId} P{p.Period}: P90 went backwards");
                prevP50 = p.P50Pct; prevLow = p.P10Pct!.Value; prevHigh = p.P90Pct!.Value;
            }
        }
    }

    [Fact]
    public void The_workbooks_one_progress_reversal_reads_as_stalled_not_as_negative_pace()
    {
        var c = ForecasterFor(Panel, Origins.ForecastPeriod)
            .Project(new[] { "BCC-COM-SEC-1216" }, null).Centres.Single();

        Assert.True(c.Stalled);
        Assert.Equal(0, c.PacePctPerPeriod);
        Assert.Null(c.ProjectedFinishPeriod);
    }

    [Fact]
    public void Measured_points_carry_no_band_because_they_are_not_projections()
    {
        var f = ForecasterFor(Panel, Origins.ForecastPeriod).Project(new[] { "BCC-STR-CON-205" }, null);
        var measured = f.Centres.Single().Points.Where(p => p.Tier == ProgressTier.Measured).ToList();

        Assert.NotEmpty(measured);
        Assert.All(measured, p =>
        {
            Assert.Equal(p.P50Pct, p.P10Pct!.Value, 6);
            Assert.Equal(p.P50Pct, p.P90Pct!.Value, 6);
        });
    }

    [Fact]
    public void Tier_flips_to_extrapolated_exactly_past_the_backtested_horizon()
    {
        var f = ForecasterFor(Panel, Origins.ForecastPeriod).Project(new[] { "BCC-STR-CON-205" }, null);
        var c = f.Centres.Single();

        Assert.Equal(Origins.ForecastPeriod + ProgressConfig.BacktestedHorizon, f.BacktestedThroughPeriod);
        foreach (var p in c.Points.Where(p => p.Period > f.OriginPeriod))
        {
            var expected = p.Period <= f.BacktestedThroughPeriod ? ProgressTier.Forecast : ProgressTier.Extrapolated;
            Assert.Equal(expected, p.Tier);
        }
        // The horizon must actually reach past the measured band, or the tier means nothing on screen.
        Assert.Contains(c.Points, p => p.Tier == ProgressTier.Extrapolated);
    }

    // ── the workbook: the claim the UI is allowed to make ──

    [Fact]
    public void Backtest_scores_four_predictors_on_identical_rows_at_every_horizon()
    {
        for (int h = 1; h <= ProgressConfig.BacktestedHorizon; h++)
        {
            var rows = Validation.Metrics.Where(m => m.Horizon == h).ToList();
            Assert.Equal(4, rows.Count);
            Assert.Single(rows.Select(m => m.N).Distinct());
            Assert.True(rows[0].N > 0);
        }
    }

    [Fact]
    public void Recent_pace_beats_assuming_the_work_stops_at_every_horizon()
    {
        // The floor. If continuing the observed pace cannot beat "nothing more happens", the feature
        // has no basis at all and should not be on screen.
        for (int h = 1; h <= ProgressConfig.BacktestedHorizon; h++)
        {
            var m = Validation.Metrics.Where(x => x.Horizon == h).ToDictionary(x => x.Predictor, x => x.MaePp);
            Assert.True(m["pace"] < m["hold"], $"h={h}: pace {m["pace"]:0.##} pp should beat hold {m["hold"]:0.##} pp");
        }
    }

    [Fact]
    public void Accuracy_stays_within_the_bound_the_ui_publishes()
    {
        // Measured at the time of writing: 1.81 / 3.19 / 3.86 pp. These bounds are headroom over that,
        // so ordinary data churn passes but a genuine regression in the method fails — the UI quotes
        // these figures as its warrant, and a silently worse projection behind the same label is the
        // failure this test exists to catch.
        var bounds = new Dictionary<int, double> { [1] = 2.5, [2] = 4.0, [3] = 4.8 };
        foreach (var (h, bound) in bounds)
        {
            var mae = Validation.Metrics.Single(m => m.Predictor == "pace" && m.Horizon == h).MaePp;
            Assert.True(mae < bound, $"h={h}: pace MAE {mae:0.##} pp exceeded the published bound {bound} pp");
        }
    }

    [Fact]
    public void Bands_widen_with_horizon_and_are_measured_from_real_residuals()
    {
        var bands = Validation.Bands.OrderBy(b => b.Horizon).ToList();
        Assert.Equal(ProgressConfig.BacktestedHorizon, bands.Count);
        Assert.All(bands, b =>
        {
            Assert.True(b.N > 0, $"h={b.Horizon} band was fitted on no residuals");
            Assert.True(b.P10 <= 0 && b.P90 >= 0, $"h={b.Horizon} band does not straddle the median");
        });

        for (int i = 1; i < bands.Count; i++)
            Assert.True(bands[i].P90 - bands[i].P10 >= bands[i - 1].P90 - bands[i - 1].P10,
                $"band narrowed from h={bands[i - 1].Horizon} to h={bands[i].Horizon}");
    }

    [Fact]
    public void The_ifc_mapped_structure_centres_top_out_inside_the_horizon()
    {
        // The eight centres the element register reaches — the ones the 4D sequence actually draws.
        // If these do not finish, the building never tops out and the feature has no payoff.
        var mapped = new[]
        {
            "BCC-STR-CON-204", "BCC-STR-CON-205", "BCC-STR-CON-206", "BCC-STR-FWK-209",
            "BCC-STR-FWK-210", "BCC-STR-FWK-211", "BCC-STR-RBR-212", "BCC-STR-RBR-214",
        };

        var f = ForecasterFor(Panel, Origins.ForecastPeriod).Project(mapped, null);

        Assert.Equal(mapped.Length, f.Centres.Count);
        Assert.All(f.Centres, c =>
        {
            Assert.False(c.Stalled, $"{c.BccId} has no pace at the origin");
            Assert.NotNull(c.ProjectedFinishPeriod);
        });

        // The suggested horizon is where the last of them finishes, and every centre reaches 100% by it.
        Assert.Equal(f.Centres.Max(c => c.ProjectedFinishPeriod!.Value), f.SuggestedHorizonPeriod);
        Assert.True(f.SuggestedHorizonPeriod > f.OriginPeriod);
        Assert.All(f.Centres, c =>
            Assert.Equal(100, c.Points.Single(p => p.Period == f.HorizonPeriod).P50Pct, 4));
    }

    [Fact]
    public void Requesting_a_subset_returns_only_that_subset_and_ignores_unknown_ids()
    {
        var f = ForecasterFor(Panel, Origins.ForecastPeriod)
            .Project(new[] { "BCC-STR-CON-205", "BCC-DOES-NOT-EXIST" }, null);

        Assert.Single(f.Centres);
        Assert.Equal("BCC-STR-CON-205", f.Centres[0].BccId);
    }

    [Fact]
    public void Alert_level_is_carried_forward_from_the_origin_never_invented()
    {
        var f = ForecasterFor(Panel, Origins.ForecastPeriod).Project(new[] { "BCC-STR-CON-205" }, null);
        var atOrigin = Panel.Single(p => p.BccId == "BCC-STR-CON-205" && p.PeriodId == Origins.ForecastPeriod);

        Assert.Equal(atOrigin.AlertLevel, f.Centres.Single().AlertAtOrigin);
    }

    [Fact]
    public void Projection_is_deterministic()
    {
        // The 4D video renderer double-renders and diffs frame checksums; a projection that varied
        // between calls would break that contract silently.
        var a = ForecasterFor(Panel, Origins.ForecastPeriod).Project(null, 20);
        var b = ForecasterFor(Panel, Origins.ForecastPeriod).Project(null, 20);

        Assert.Equal(a.Centres.Count, b.Centres.Count);
        foreach (var (x, y) in a.Centres.Zip(b.Centres))
        {
            Assert.Equal(x.BccId, y.BccId);
            Assert.Equal(x.PacePctPerPeriod, y.PacePctPerPeriod);
            Assert.Equal(x.ProjectedFinishPeriod, y.ProjectedFinishPeriod);
            // Records compare structurally, so this covers every period, band endpoint and tier.
            Assert.Equal(x.Points, y.Points);
        }
    }
}
