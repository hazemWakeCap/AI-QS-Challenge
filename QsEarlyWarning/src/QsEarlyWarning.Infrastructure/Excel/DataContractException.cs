namespace QsEarlyWarning.Infrastructure.Excel;

/// <summary>Thrown when the workbook fails production schema/semantic validation. Plan §6.2.</summary>
public sealed class DataContractException : Exception
{
    public DataContractException(string message) : base(message) { }
}
