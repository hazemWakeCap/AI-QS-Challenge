using QsEarlyWarning.Domain.Entities;

namespace QsEarlyWarning.Core.Forecasting;

/// <summary>
/// A deterministic unit-rate "what-if" (idea-4 scenario surface). Given a cost centre at the latest
/// origin and a user-supplied AED/unit rate, it reprices the centre's REMAINING quantity at that rate
/// while keeping the centre's recent physical pace (a price renegotiation changes cost, not tempo),
/// and reports the next-3-period spend path plus scenario cost-to-complete / final cost / VAC.
///
/// This is NOT a validated forecast: every number is a direct arithmetic consequence of the stated
/// assumption. All arithmetic lives here (the copilot narrates, it never computes). The only external
/// input is the user's rate; it is echoed back so the assumption is auditable.
/// </summary>
public static class ScenarioForecaster
{
    private const int Horizons = 3;

    public static ScenarioForecast Rate(
        IReadOnlyList<CostCentrePeriod> panel, string bccId, int origin, double newRate, int? effectiveFrom)
    {
        int effective = Math.Max(origin + 1, effectiveFrom ?? origin + 1);

        if (!double.IsFinite(newRate) || newRate <= 0)
            return ScenarioForecast.Unavailable(bccId, origin, newRate, effective, "A positive unit rate is required.");

        var byPeriod = panel
            .Where(p => string.Equals(p.BccId, bccId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.PeriodId)
            .ToDictionary(g => g.Key, g => g.First());

        if (!byPeriod.TryGetValue(origin, out var cur))
            return ScenarioForecast.Unavailable(bccId, origin, newRate, effective, $"No row for {bccId} at period {origin}.");

        double? bac = Fin(cur.BacAed);
        double? earned = Fin(cur.EarnedQtyCumul);
        double? ac0 = Fin(cur.AcCumulative);
        double? actPct = Fin(cur.ActualPctComplete);

        // Budget quantity: loaded Budget_Qty, else derived from earned qty ÷ actual-percent-complete.
        double? budgetQty = Fin(cur.BudgetQty);
        if (budgetQty is not > 0 && earned is double e && e > 0 && actPct is double a && a > 0)
            budgetQty = e / (a / 100.0);

        if (budgetQty is not double bq || bq <= 0)
            return ScenarioForecast.Unavailable(bccId, origin, newRate, effective,
                "Budget quantity is unavailable and cannot be derived (need Budget_Qty, or earned qty at a non-zero percent complete).");
        if (earned is not double earnedQty || earnedQty < 0)
            return ScenarioForecast.Unavailable(bccId, origin, newRate, effective, "Earned quantity is unavailable at this period.");

        double remainingQty = bq - earnedQty;
        if (remainingQty <= 0)
            return ScenarioForecast.Unavailable(bccId, origin, newRate, effective, "No remaining quantity — the centre is complete.");

        double? pace = IncrementHelper.RecentQtyPace(byPeriod, origin);
        if (pace is not double pacePerPeriod || pacePerPeriod <= 0)
            return ScenarioForecast.Unavailable(bccId, origin, newRate, effective,
                "No recent physical pace to project (need adjacent periods with a positive earned-quantity increment).");

        double? plannedRate = bac is double b && b > 0 ? b / bq : null;
        double? currentRate = earnedQty > 0 && ac0 is double c ? c / earnedQty : null;
        double rateBeforeSwitch = currentRate ?? newRate;   // pre-switch periods keep the realized rate

        // Per-period repriced increments for the next 3 periods (a prefix of the full to-complete walk).
        var increments = new List<ScenarioPeriodSpend>(Horizons);
        double remainingLeft = remainingQty;
        for (int h = 1; h <= Horizons; h++)
        {
            int p = origin + h;
            double rate = p >= effective ? newRate : rateBeforeSwitch;
            double qty = Math.Min(pacePerPeriod, remainingLeft);
            increments.Add(new ScenarioPeriodSpend(p, Rnd(qty), Rnd(qty * rate), Rnd(rate)));
            remainingLeft -= qty;
            if (remainingLeft <= 0) break;
        }

        // Cost to finish ALL remaining work (analytic, not just the 3-period window): quantity built
        // before the rate takes effect stays at the realized rate; the rest is at the new rate.
        double periodsBeforeSwitch = Math.Max(0, effective - (origin + 1));
        double qtyBeforeSwitch = Math.Min(remainingQty, periodsBeforeSwitch * pacePerPeriod);
        double qtyAfterSwitch = remainingQty - qtyBeforeSwitch;
        double costToComplete = qtyBeforeSwitch * rateBeforeSwitch + qtyAfterSwitch * newRate;
        double? finalCost = ac0 is double acCur ? acCur + costToComplete : null;
        double? vac = finalCost is double fc && bac is double bb ? bb - fc : null;

        return new ScenarioForecast
        {
            Available = true,
            BccId = bccId,
            OriginPeriod = origin,
            Unit = cur.Unit,
            NewUnitRate = Rnd(newRate),
            EffectiveFromPeriod = effective,
            BudgetQty = Rnd(bq),
            RemainingQty = Rnd(remainingQty),
            PlannedUnitRate = Rnd(plannedRate),
            CurrentRealizedRate = Rnd(currentRate),
            RecentQtyPacePerPeriod = Rnd(pacePerPeriod),
            Increments = increments,
            ScenarioCostToComplete = Rnd(costToComplete),
            ScenarioFinalCost = finalCost is double f ? Rnd(f) : 0,
            ScenarioVac = vac is double v ? Rnd(v) : null,
        };
    }

    private static double? Fin(double? v) => v is double d && double.IsFinite(d) ? d : null;
    private static double Rnd(double v) => double.IsFinite(v) ? Math.Round(v, 3) : 0;
    private static double? Rnd(double? v) => v is double d && double.IsFinite(d) ? Math.Round(d, 3) : null;
}
