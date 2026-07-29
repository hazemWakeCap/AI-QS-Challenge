namespace QsEarlyWarning.Tests;

/// <summary>Locates the real workbook by walking up from the test assembly to the repo root.</summary>
public static class TestData
{
    public static string WorkbookPath { get; } = Locate();

    /// <summary>The authored IFC-element → BOQ-item register, which lives beside the workbook.</summary>
    public static string ElementMapPath { get; } =
        Path.Combine(Path.GetDirectoryName(WorkbookPath)!, "ifc_boq_map.csv");

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "Tower_X_Project_Data.xlsx");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate data/Tower_X_Project_Data.xlsx above the test bin dir.");
    }
}
