namespace QsEarlyWarning.Domain.Estimate;

/// <summary>
/// Supplies the authored IFC-element → BOQ-item register for a project.
///
/// Project-gated like <see cref="IEstimateSource"/>: the register binds one specific model to one
/// specific bill, so it is only returned for the project that bill belongs to. Any other project
/// gets null and the feature is simply absent rather than wrong.
/// </summary>
public interface IIfcElementMapSource
{
    /// <summary>The register, or null when this project has none.</summary>
    IfcElementMap? TryLoadForProject(long projectId);
}
