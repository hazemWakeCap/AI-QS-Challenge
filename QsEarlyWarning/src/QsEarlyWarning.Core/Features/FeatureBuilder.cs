using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Features;

/// <summary>Outcome of pairing a period, with excluded-count bookkeeping. Plan §6.3.</summary>
public sealed record PairingResult
{
    public required IReadOnlyList<TransitionPair> Pairs { get; init; }
    /// <summary>GREEN-at-p rows whose successor was NOT STARTED / missing / invalid — excluded, counted.</summary>
    public required int ExcludedCount { get; init; }
}

/// <summary>
/// Builds GREEN→(AMBER?) transition pairs and engineers features. Plan §6.3.
///
/// Adjacency is EXPLICIT: a pair exists only where next.PeriodId == current.PeriodId + 1.
/// Lag deltas require exact p−1 / p−2 predecessors; otherwise null (never differenced across a gap).
///
/// Eligible-population + label contract:
///   - current row eligible iff GREEN and scoreable (finite CPI/gap inputs)
///   - Label = successor is AMBER
///   - successor GREEN/CLOSED → negative (kept, y=false)
///   - successor NOT STARTED / missing / invalid → excluded and counted
/// </summary>
public sealed class FeatureBuilder
{
    /// <summary>
    /// Live centres grouped by (period, zone) — the neighbourhood a peer feature draws on.
    ///
    /// "Live" means the centre is actually spending: GREEN or AMBER with a finite CPI. NOT STARTED
    /// and CLOSED rows are excluded because a dormant neighbour carries no information about how
    /// the neighbourhood is performing, and a closed one is no longer at risk.
    /// </summary>
    private sealed class PeerIndex
    {
        private readonly Dictionary<(int Period, string Zone), List<CostCentrePeriod>> _byZone = new();

        public PeerIndex(IReadOnlyList<CostCentrePeriod> panel)
        {
            foreach (var r in panel)
            {
                if (string.IsNullOrWhiteSpace(r.ZoneArea)) continue;
                if (r.Cpi is not double cpi || !double.IsFinite(cpi)) continue;
                if (!IsLive(r.AlertLevel)) continue;

                var key = (r.PeriodId, r.ZoneArea!.Trim().ToUpperInvariant());
                if (!_byZone.TryGetValue(key, out var list))
                    _byZone[key] = list = new List<CostCentrePeriod>();
                list.Add(r);
            }
        }

        /// <summary>
        /// Aggregate CPI of the neighbourhood, excluding <paramref name="self"/> and — when
        /// <paramref name="crossTradeOnly"/> — every centre of the same discipline.
        /// Returns (null, 0) when there is no neighbourhood to judge.
        /// </summary>
        public (double? Cpi, int Count) PeerCpi(CostCentrePeriod self, bool crossTradeOnly)
        {
            if (string.IsNullOrWhiteSpace(self.ZoneArea)) return (null, 0);
            var key = (self.PeriodId, self.ZoneArea!.Trim().ToUpperInvariant());
            if (!_byZone.TryGetValue(key, out var zone)) return (null, 0);

            double ev = 0, ac = 0;
            int n = 0;
            foreach (var peer in zone)
            {
                if (string.Equals(peer.BccId, self.BccId, StringComparison.Ordinal)) continue;   // leave-one-out
                if (crossTradeOnly && Eq(peer.Discipline, self.Discipline ?? "")) continue;

                ev += peer.EvAed ?? 0;
                ac += peer.AcCumulative ?? 0;
                n++;
            }

            if (n == 0 || ac <= 0) return (null, n);
            return (ev / ac, n);
        }

        private static bool IsLive(string? alert) => Eq(alert, "GREEN") || Eq(alert, "AMBER");
    }

    /// <summary>Builds pairs for a single feature period p across all centres.</summary>
    public PairingResult BuildPairsForPeriod(IReadOnlyList<CostCentrePeriod> panel, int p)
    {
        var byBcc = panel
            .GroupBy(r => r.BccId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.PeriodId), StringComparer.Ordinal);

        var peers = new PeerIndex(panel);
        var pairs = new List<TransitionPair>();
        var excluded = 0;

        foreach (var (_, periods) in byBcc)
        {
            if (!periods.TryGetValue(p, out var cur) || !cur.IsScoreableGreen)
                continue;

            // Explicit successor at exactly p+1.
            if (!periods.TryGetValue(p + 1, out var next) || next.AlertLevel is null)
            {
                excluded++;
                continue;
            }

            var succ = next.AlertLevel;
            bool isAmber = Eq(succ, "AMBER");
            bool isNegative = Eq(succ, "GREEN") || Eq(succ, "CLOSED");

            if (!isAmber && !isNegative)
            {
                // NOT STARTED / anything else → excluded and counted.
                excluded++;
                continue;
            }

            pairs.Add(Engineer(cur, isAmber, periods, peers));
        }

        return new PairingResult { Pairs = pairs, ExcludedCount = excluded };
    }

    /// <summary>
    /// Engineers feature rows for scoring a period's GREEN-at-p population — WITHOUT a successor.
    /// Used to produce a watchlist (the label is unknown/irrelevant at scoring time, so the
    /// forecast period 12, which has no successor, still yields rows). Plan §6.7.
    /// </summary>
    public IReadOnlyList<TransitionPair> BuildScoringRows(IReadOnlyList<CostCentrePeriod> panel, int p)
    {
        var byBcc = panel
            .GroupBy(r => r.BccId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.PeriodId), StringComparer.Ordinal);

        var peers = new PeerIndex(panel);
        var rows = new List<TransitionPair>();
        foreach (var (_, periods) in byBcc)
        {
            if (!periods.TryGetValue(p, out var cur) || !cur.IsScoreableGreen)
                continue;
            rows.Add(Engineer(cur, label: false, periods, peers)); // label unused for scoring
        }
        return rows;
    }

    /// <summary>Builds pairs for all feature periods in [minP, maxP].</summary>
    public PairingResult BuildPairs(IReadOnlyList<CostCentrePeriod> panel, int minP, int maxP)
    {
        var all = new List<TransitionPair>();
        var excluded = 0;
        for (int p = minP; p <= maxP; p++)
        {
            var r = BuildPairsForPeriod(panel, p);
            all.AddRange(r.Pairs);
            excluded += r.ExcludedCount;
        }
        return new PairingResult { Pairs = all, ExcludedCount = excluded };
    }

    private static TransitionPair Engineer(
        CostCentrePeriod cur, bool label, Dictionary<int, CostCentrePeriod> periods, PeerIndex peers)
    {
        var p = cur.PeriodId;
        double gap = cur.Gap!.Value;

        // Lag deltas: exact-predecessor only.
        double? dCpi1 = Delta(periods, p, p - 1, r => r.Cpi);
        double? dGap1 = Delta(periods, p, p - 1, r => r.Gap);
        double? dCpi2 = Delta(periods, p, p - 2, r => r.Cpi);

        double? acCum = cur.AcCumulative;
        double? Share(double? part) =>
            part is double v && acCum is double d && d != 0 ? v / d : null;

        var (peerCpi, peerCount) = peers.PeerCpi(cur, crossTradeOnly: false);
        var (crossCpi, crossCount) = peers.PeerCpi(cur, crossTradeOnly: true);

        return new TransitionPair
        {
            BccId = cur.BccId,
            PeriodId = p,
            Discipline = cur.Discipline,
            PackageCode = cur.PackageCode,
            ZoneArea = cur.ZoneArea,
            Label = label,
            Cpi = cur.Cpi!.Value,
            Rolling3mCpi = cur.Rolling3mCpi,
            Spi = cur.Spi,
            VariancePct = cur.VariancePct,
            EacVsBacRatio = cur.EacVsBacRatio,
            Gap = gap,
            DCpi1 = dCpi1,
            DGap1 = dGap1,
            DCpi2 = dCpi2,
            PeerCpi = peerCpi,
            PeerCount = peerCount,
            PeerCpiCrossTrade = crossCpi,
            CrossTradePeerCount = crossCount,
            ShareMaterial = Share(cur.AcMaterial),
            ShareManpower = Share(cur.AcManpower),
            ShareEquipment = Share(cur.AcEquipment),
            ShareSubcontract = Share(cur.AcSubcontract),
        };
    }

    /// <summary>
    /// value(p) − value(pred) only when an EXACT predecessor period exists, is not NOT STARTED,
    /// and both values are present. Otherwise null — never differenced across a gap.
    /// </summary>
    private static double? Delta(
        Dictionary<int, CostCentrePeriod> periods, int p, int predPeriod, Func<CostCentrePeriod, double?> sel)
    {
        if (predPeriod < 1) return null;
        if (!periods.TryGetValue(predPeriod, out var pred)) return null;
        if (Eq(pred.AlertLevel, "NOT STARTED") || pred.AlertLevel is null) return null;
        var now = sel(periods[p]);
        var before = sel(pred);
        return now is double a && before is double b ? a - b : null;
    }

    private static bool Eq(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
