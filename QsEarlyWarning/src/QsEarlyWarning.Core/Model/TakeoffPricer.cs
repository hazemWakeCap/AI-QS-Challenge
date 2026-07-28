namespace QsEarlyWarning.Core.Model;

/// <summary>A quantity measured off a model, before any money is attached.</summary>
/// <param name="IfcClass">Upper-case IFC entity name.</param>
/// <param name="Measure">Volume or area.</param>
/// <param name="Quantity">The measured amount, in m³ or m².</param>
/// <param name="ElementCount">How many elements contributed it.</param>
/// <param name="UnmeasuredCount">Elements of this class carrying no such measurement. These are
/// counted so the report can say what it could not see, rather than implying full coverage.</param>
public sealed record TakeoffLine(
    string IfcClass,
    TakeoffMeasure Measure,
    double Quantity,
    int ElementCount,
    int UnmeasuredCount);

/// <summary>A measured quantity that a rate was found for.</summary>
public sealed record PricedLine(
    string IfcClass, TakeoffMeasure Measure, double Quantity, string Unit, int ElementCount,
    string BoqItemRef, string? BoqDescription, double UnitRate, double Amount, string Rationale);

/// <summary>A measured quantity that could not be priced, and why.</summary>
public sealed record UnpricedLine(
    string IfcClass, TakeoffMeasure Measure, double Quantity, int ElementCount, string Reason);

/// <summary>
/// The result of pricing a model take-off against a rate library.
///
/// <para><b>The residual is not an afterthought.</b> <see cref="PricedAmount"/> on its own is a
/// misleading number — it is the cost of the part of the building we could both measure and price.
/// It is only meaningful next to what it excludes, so the two always travel together.</para>
///
/// <para><see cref="TiesOut"/> checks <c>PricedElements + UnpricedElements + UnmeasuredElements ==
/// TotalElements</c>, where <c>TotalElements</c> is the count the measurer independently reported
/// for the model. When it is false, elements fell out of the take-off unaccounted for and the
/// priced figure is understating the building — the UI must say so rather than show a clean total.</para>
/// </summary>
public sealed record TakeoffPricing(
    string Currency,
    double PricedAmount,
    IReadOnlyList<PricedLine> Priced,
    IReadOnlyList<UnpricedLine> Unpriced,
    int TotalElements,
    int PricedElements,
    int UnpricedElements,
    int UnmeasuredElements,
    bool TiesOut,
    IReadOnlyList<TakeoffRule> RulesApplied);

/// <summary>
/// Prices a model take-off with the project's own rate library.
///
/// <para>All money arithmetic happens here in C#, never in the browser and never in the model —
/// the same rule the copilot follows. The client measures geometry; this decides what it costs.</para>
/// </summary>
public static class TakeoffPricer
{
    /// <param name="modelElementCount">How many elements the model actually contains, reported by
    /// the measurer. The tie-out checks the accounted-for elements against this independent number —
    /// comparing the parts against their own sum would assert nothing.</param>
    public static TakeoffPricing Price(
        IReadOnlyList<TakeoffLine> lines,
        RateBook rates,
        string currency,
        int modelElementCount)
    {
        var priced = new List<PricedLine>();
        var unpriced = new List<UnpricedLine>();

        int pricedElements = 0, unpricedElements = 0, unmeasuredElements = 0;

        foreach (var line in lines)
        {
            unmeasuredElements += Math.Max(0, line.UnmeasuredCount);

            // A zero or negative measurement prices nothing; report it rather than emitting AED 0.
            if (line.Quantity <= 0)
            {
                if (line.ElementCount > 0)
                {
                    unpriced.Add(new UnpricedLine(line.IfcClass, line.Measure, line.Quantity,
                        line.ElementCount, "No measurable quantity for this class in the model."));
                    unpricedElements += line.ElementCount;
                }
                continue;
            }

            var rule = TakeoffRateMap.Find(line.IfcClass, line.Measure);
            var rate = rule is null ? null : rates.Find(rule.BoqItemRef);

            if (rule is null || rate is null)
            {
                var reason = rule is null
                    ? TakeoffRateMap.WhyUnpriced(line.IfcClass)
                      ?? $"No rule maps {line.IfcClass} ({Label(line.Measure)}) to a BOQ item."
                    : $"Rule points at BOQ item {rule.BoqItemRef}, which carries no unit rate in this library.";

                unpriced.Add(new UnpricedLine(line.IfcClass, line.Measure, line.Quantity, line.ElementCount, reason));
                unpricedElements += line.ElementCount;
                continue;
            }

            // Guard the unit: pricing m³ at an m² rate is silent nonsense, so it is refused.
            if (!UnitsAgree(rule.Unit, rate.Unit))
            {
                unpriced.Add(new UnpricedLine(line.IfcClass, line.Measure, line.Quantity, line.ElementCount,
                    $"Unit mismatch — measured in {rule.Unit}, but BOQ item {rule.BoqItemRef} is priced per {rate.Unit}."));
                unpricedElements += line.ElementCount;
                continue;
            }

            priced.Add(new PricedLine(
                line.IfcClass, line.Measure, line.Quantity, rule.Unit, line.ElementCount,
                rate.ItemRef, rate.Description, rate.UnitRate,
                Math.Round(line.Quantity * rate.UnitRate, 2), rule.Rationale));

            pricedElements += line.ElementCount;
        }

        int accountedFor = pricedElements + unpricedElements + unmeasuredElements;

        return new TakeoffPricing(
            currency,
            Math.Round(priced.Sum(p => p.Amount), 2),
            priced.OrderByDescending(p => p.Amount).ToList(),
            unpriced.OrderByDescending(u => u.ElementCount).ToList(),
            modelElementCount, pricedElements, unpricedElements, unmeasuredElements,
            TiesOut: accountedFor == modelElementCount,
            TakeoffRateMap.Rules);
    }

    /// <summary>Units agree when they normalise to the same token (m3/m³, m2/m², sqm, cum).</summary>
    private static bool UnitsAgree(string ruleUnit, string? boqUnit) =>
        Normalise(ruleUnit) == Normalise(boqUnit);

    private static string Normalise(string? unit) =>
        (unit ?? "").Trim().ToLowerInvariant()
            .Replace("³", "3").Replace("²", "2")
            .Replace("cum", "m3").Replace("sqm", "m2")
            .Replace(" ", "");

    private static string Label(TakeoffMeasure m) => m == TakeoffMeasure.Volume ? "volume" : "area";
}
