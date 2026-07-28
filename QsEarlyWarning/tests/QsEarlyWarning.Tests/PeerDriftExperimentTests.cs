using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Core.Features;
using QsEarlyWarning.Core.Scoring;
using QsEarlyWarning.Domain.Constants;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;
using Xunit;
using Xunit.Abstractions;

namespace QsEarlyWarning.Tests;

/// <summary>
/// The peer-drift experiment: do a cost centre's neighbours predict its drift?
///
/// <para>The question we set out to ask was spatial. It could not be asked — see
/// <see cref="Zone_is_a_coarsening_of_discipline"/> — so it is asked twice, correctly labelled:
/// once about TRADE peers (well-powered) and once about genuinely spatial cross-trade peers
/// (only definable in FLOORS-ALL, and underpowered). Both are descriptive; the deployed scorer is
/// unchanged unless a challenger earns it out-of-fold.</para>
/// </summary>
public sealed class PeerDriftExperimentTests
{
    private readonly ITestOutputHelper _out;
    public PeerDriftExperimentTests(ITestOutputHelper output) => _out = output;

    private static readonly CostCentrePeriod[] Panel =
        new ExcelPanelLoader().Load(TestData.WorkbookPath).ToArray();

    private static readonly TrainedModel Model = new RollingOriginEvaluator().Train(Panel);

    // ── the finding that forced the re-frame ──

    [Fact]
    public void Zone_is_a_coarsening_of_discipline()
    {
        var report = ZoneDisciplineCollinearity.Measure(Panel);

        _out.WriteLine(report.Verdict);
        foreach (var z in report.Zones)
            _out.WriteLine($"  {z.ZoneArea,-18} {z.CentreCount,3} centres  {z.DisciplineCount} discipline(s)");

        // The load-bearing fact: knowing WHERE tells you nothing that knowing WHO did not.
        Assert.Equal(0, report.DisciplinesSpanningZones);
        Assert.True(report.ZoneIsProxyForDiscipline);
        Assert.True(report.SingleDisciplineZones >= 8,
            $"expected most zones to be single-discipline, got {report.SingleDisciplineZones}/{report.ZoneCount}");
    }

    [Fact]
    public void Floors_all_is_the_only_place_a_spatial_signal_can_be_isolated()
    {
        var report = ZoneDisciplineCollinearity.Measure(Panel);

        Assert.Equal("FLOORS-ALL", report.MostMixedZone);
        Assert.True(report.MostMixedZoneDisciplines >= 7,
            $"FLOORS-ALL should mix many trades, got {report.MostMixedZoneDisciplines}");
    }

    // ── the peer features ──

    [Fact]
    public void Peer_cpi_excludes_the_centre_itself()
    {
        var pairs = new FeatureBuilder().BuildPairs(Panel, 4, 11).Pairs;

        // A centre's own CPI cannot be the sole source of its peer CPI: with peers present the two
        // must be independently computed, so an exact match across the board would signal self-leak.
        var withPeers = pairs.Where(p => p.PeerCount > 0 && p.PeerCpi is not null).ToList();
        Assert.NotEmpty(withPeers);
        Assert.Contains(withPeers, p => Math.Abs(p.PeerCpi!.Value - p.Cpi) > 1e-9);
    }

    [Fact]
    public void A_centre_with_no_peers_gets_null_not_zero()
    {
        // Zero would rank a centre as if its neighbourhood were in freefall. Absence of a
        // neighbourhood must be absence of a signal.
        var pairs = new FeatureBuilder().BuildPairs(Panel, 4, 11).Pairs;

        Assert.All(pairs, p =>
        {
            if (p.PeerCount == 0) Assert.Null(p.PeerCpi);
            if (p.CrossTradePeerCount == 0) Assert.Null(p.PeerCpiCrossTrade);
        });
    }

    [Fact]
    public void Cross_trade_peers_exist_only_where_trades_actually_mix()
    {
        var pairs = new FeatureBuilder().BuildPairs(Panel, 4, 11).Pairs;
        var withCrossTrade = pairs.Where(p => p.CrossTradePeerCount > 0).ToList();

        Assert.NotEmpty(withCrossTrade);
        // Every one of them is in FLOORS-ALL — the direct consequence of the collinearity.
        Assert.All(withCrossTrade, p => Assert.Equal("FLOORS-ALL", p.ZoneArea));

        _out.WriteLine($"cross-trade peers available on {withCrossTrade.Count} of {pairs.Count} rows "
                     + $"({100.0 * withCrossTrade.Count / pairs.Count:0}%) — all in FLOORS-ALL");
    }

    // ── the experiment ──

    [Fact]
    public void Challengers_are_evaluated_on_exactly_the_same_folds_as_the_rule()
    {
        var summary = Model.Summary;
        Assert.NotNull(summary.Challenger);

        var rule = summary.Rule.Single(r => r.K == EvmThresholds.SelectionK);
        foreach (var ch in summary.Challenger!.Where(c => c.K == EvmThresholds.SelectionK))
        {
            Assert.Equal(rule.Folds.Count, ch.Folds.Count);
            Assert.Equal(
                rule.Folds.Select(f => f.PeriodId).ToArray(),
                ch.Folds.Select(f => f.PeriodId).ToArray());
            // Identical ranked population — the challenger reorders, it never filters.
            Assert.Equal(
                rule.Folds.Select(f => f.Eligible).ToArray(),
                ch.Folds.Select(f => f.Eligible).ToArray());
        }
    }

    [Fact]
    public void The_experiment_does_not_leak_into_what_is_served()
    {
        // The whole point of a descriptive challenger: nothing about deployment moved.
        Assert.Equal("RuleRiskScore@v1", Model.Summary.ScorerVersion);
        Assert.All(Model.Artifacts.Values, a => Assert.Equal("RuleRiskScore@v1", RuleArtifact.ScorerVersion));
    }

    [Fact]
    public void Report_the_result_whatever_it_is()
    {
        var summary = Model.Summary;
        int k = EvmThresholds.SelectionK;

        var rule = summary.Rule.Single(r => r.K == k);
        var bestCpi = summary.CpiNative.Where(r => r.K == k).Max(r => r.MacroPrecision ?? 0);

        _out.WriteLine($"folds={summary.FoldCount}  transitions={summary.TotalTransitions}  k={k}");
        _out.WriteLine($"  {"rule (deployed)",-26} {rule.MacroPrecision:P1}");
        _out.WriteLine($"      per fold: {string.Join(" ", rule.Folds.Select(f => $"P{f.PeriodId}={f.Precision:P0}"))}");
        _out.WriteLine($"  {"best cpi-native baseline",-26} {bestCpi:P1}");
        _out.WriteLine($"  decisions behind each figure: {summary.FoldCount} folds x k={k} = "
                     + $"{summary.FoldCount * k} ranked slots — small, so read the per-fold spread, not the mean alone.");

        foreach (var ch in summary.Challenger!.Where(c => c.K == k))
        {
            var delta = (ch.MacroPrecision ?? 0) - (rule.MacroPrecision ?? 0);
            var verdict = delta > 1e-9 ? "BEATS the rule" : delta < -1e-9 ? "loses to the rule" : "ties the rule";
            _out.WriteLine($"  {ch.ScorerLabel,-26} {ch.MacroPrecision:P1}  ({delta:+0.0%;-0.0%;0.0%}) — {verdict}");
            _out.WriteLine($"      per fold: {string.Join(" ", ch.Folds.Select(f => $"P{f.PeriodId}={f.Precision:P0}"))}");
        }

        // Deliberately asserts presence and comparability, NOT superiority. Asserting that a
        // challenger wins would make the test a wish rather than a measurement.
        Assert.NotEmpty(summary.Challenger!);
        Assert.All(summary.Challenger!, c => Assert.NotEmpty(c.Folds));
    }
}
