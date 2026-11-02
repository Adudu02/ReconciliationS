namespace Reconciliation.Core.Ingestion;

public enum ImportStatus { Imported, AlreadyImported, Rejected, Importing }
public enum ImportFileKind { Cfdi, BankStatement }

public static class ImportDatabaseValues
{
    public static string ToDatabaseValue(this ImportStatus status) => status switch
    {
        ImportStatus.Imported => "imported",
        ImportStatus.AlreadyImported => "already_imported",
        ImportStatus.Rejected => "rejected",
        ImportStatus.Importing => "processing",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
    public static string ToDatabaseValue(this ImportFileKind kind) => kind switch
    {
        ImportFileKind.Cfdi => "cfdi",
        ImportFileKind.BankStatement => "bank-statement",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
