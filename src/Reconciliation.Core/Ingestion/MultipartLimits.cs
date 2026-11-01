namespace Reconciliation.Core.Ingestion;

public static class MultipartLimits
{
    public const int BoundaryLength = 128;
    public const int FieldCount = 8;
    public const int FieldBytes = 256;
    public const int BufferBytes = 8192;
    public const int InitialFileCapacity = 65536;
    public const int FileNameCharacters = 255;
}
