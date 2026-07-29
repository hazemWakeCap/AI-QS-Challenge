namespace QsEarlyWarning.Domain.Estimate;

/// <summary>One IFC element, and the BOQ items it was declared to consume.</summary>
/// <param name="BoqItemRefs">Empty when the bill prices nothing for this element — see
/// <see cref="IfcElementMap.Unmapped"/> for why.</param>
/// <param name="Confidence">The weakest confidence among the rules that produced this element's
/// rows. An element is only as well-placed as its shakiest binding.</param>
public sealed record MappedElement(
    string GlobalId,
    string IfcClass,
    string? Storey,
    IReadOnlyList<string> BoqItemRefs,
    double Confidence);

/// <summary>
/// One declared binding from an IFC class to a BOQ item.
///
/// <para>This is the argument, so it ships to the UI — the same principle <c>TakeoffRateMap</c>
/// follows. A QS must be able to read "a column's volume is priced by the column-concrete rate"
/// and disagree with it.</para>
/// </summary>
public sealed record MappingRule(
    string IfcClass,
    string BoqItemRef,
    string Role,
    string Basis,
    double Confidence,
    int ElementCount);

/// <summary>A class the model contains that the bill prices nothing for.</summary>
public sealed record UnmappedClass(string IfcClass, int ElementCount, string Reason);

/// <summary>
/// The authored binding between a model's elements and the bill of quantities.
///
/// <para><b>Why this has to be authored at all.</b> A real IFC export carries no cost codes — not
/// one of <c>school_str.ifc</c>'s 1,526 elements names a BOQ item — so nothing in a model can reach
/// money until someone declares the binding. This is that declaration, read from a sidecar file
/// rather than invented at runtime, so it can be reviewed, diffed and argued with.</para>
///
/// <para><b>Why one authored hop is worth it.</b> <c>9_HISTORICAL_DATA.WBS_Code</c> IS the BOQ
/// <c>Item Ref</c> — exact, bijective, 173 for 173. So declaring <c>element → BOQ item</c> buys
/// <c>element → BOQ item → cost centre → twelve periods of earned value</c>, and every hop after
/// the first is genuine workbook data.</para>
///
/// <para><b>What it deliberately does not do.</b> Elements the bill prices nothing for stay
/// unmapped and are reported as a scope gap. Pointing 375 beams at the slab item so the picture
/// fills in would attach cost to work the estimate never contained.</para>
/// </summary>
public sealed class IfcElementMap
{
    private readonly Dictionary<string, MappedElement> _byGlobalId;

    public IfcElementMap(
        IReadOnlyList<MappedElement> elements,
        IReadOnlyList<MappingRule> rules,
        IReadOnlyList<UnmappedClass> unmapped)
    {
        Elements = elements;
        Rules = rules;
        Unmapped = unmapped;
        _byGlobalId = elements.ToDictionary(e => e.GlobalId, StringComparer.Ordinal);
    }

    /// <summary>Every element in the register, mapped and unmapped alike.</summary>
    public IReadOnlyList<MappedElement> Elements { get; }

    public IReadOnlyList<MappingRule> Rules { get; }

    public IReadOnlyList<UnmappedClass> Unmapped { get; }

    /// <summary>Elements that reached at least one BOQ item.</summary>
    public int MappedCount => Elements.Count(e => e.BoqItemRefs.Count > 0);

    public int TotalCount => Elements.Count;

    /// <summary>The distinct BOQ items this model reaches.</summary>
    public IReadOnlyList<string> BoqItemRefs => Elements
        .SelectMany(e => e.BoqItemRefs)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(r => r, StringComparer.Ordinal)
        .ToList();

    public MappedElement? Find(string globalId) =>
        _byGlobalId.TryGetValue(globalId, out var e) ? e : null;
}
