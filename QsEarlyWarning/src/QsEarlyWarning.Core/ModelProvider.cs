using QsEarlyWarning.Core.Evaluation;
using QsEarlyWarning.Domain.Entities;
using QsEarlyWarning.Infrastructure.Excel;

namespace QsEarlyWarning.Core;

/// <summary>Immutable loaded+trained snapshot (panel + model). Swapped atomically on reload.</summary>
public sealed record ModelSnapshot
{
    public required IReadOnlyList<CostCentrePeriod> Panel { get; init; }
    public required TrainedModel Model { get; init; }
    public required string WorkbookPath { get; init; }

    public int RowCount => Panel.Count;
    public int CentreCount => Panel.Select(p => p.BccId).Distinct(StringComparer.Ordinal).Count();
}

public interface IModelProvider
{
    ModelSnapshot Current { get; }
    /// <summary>Rebuilds panel + all artifacts from the workbook and swaps atomically. Throws on invalid workbook.</summary>
    void Reload();
}

/// <summary>
/// Builds and holds the model snapshot (plan §6.9). Loads once at construction; Reload rebuilds
/// everything and swaps atomically, keeping the last-known-good snapshot if the new one fails.
/// </summary>
public sealed class ModelProvider : IModelProvider
{
    private readonly IPanelLoader _loader;
    private readonly string _workbookPath;
    private volatile ModelSnapshot _current;

    public ModelProvider(IPanelLoader loader, string workbookPath)
    {
        _loader = loader;
        _workbookPath = workbookPath;
        _current = Build(); // fail loud at startup if the workbook is invalid
    }

    public ModelSnapshot Current => _current;

    public void Reload()
    {
        var rebuilt = Build();   // throws → old snapshot retained
        _current = rebuilt;      // atomic reference swap
    }

    private ModelSnapshot Build()
    {
        var panel = _loader.Load(_workbookPath);
        var model = new RollingOriginEvaluator().Train(panel);
        return new ModelSnapshot { Panel = panel, Model = model, WorkbookPath = _workbookPath };
    }
}
