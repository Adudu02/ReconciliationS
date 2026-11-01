using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Reconciliation.Core.Ingestion;

internal sealed record MultipartUpload(IReadOnlyList<UploadedFile> Files, IReadOnlyDictionary<string, string> Fields)
{
    public static async Task<MultipartUpload> ReadAsync(HttpRequest request, IngestionOptions options, CancellationToken cancellationToken)
    {
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = options.MaxUploadBytes;
        if (request.ContentLength > options.MaxUploadBytes) throw new BadHttpRequestException(UploadErrors.TotalLimit, StatusCodes.Status413PayloadTooLarge);
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) || !string.Equals(contentType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Expected multipart/form-data.", StatusCodes.Status415UnsupportedMediaType);
        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > MultipartLimits.BoundaryLength) throw new BadHttpRequestException("Invalid multipart boundary.");
        using var limitedBody = new UploadReadStream(request.Body, options.MaxUploadBytes);
        var reader = new MultipartReader(boundary, limitedBody);
        var files = new List<UploadedFile>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var buffer = new byte[MultipartLimits.BufferBytes];
        while (await reader.ReadNextSectionAsync(cancellationToken) is { } part)
        {
            if (!ContentDispositionHeaderValue.TryParse(part.ContentDisposition, out var disposition))
                throw new BadHttpRequestException("Invalid multipart content disposition.");
            var partName = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
            var clientFileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            if (clientFileName is null)
            {
                if (fields.Count >= MultipartLimits.FieldCount || fields.ContainsKey(partName)) throw new BadHttpRequestException("Too many or duplicate multipart fields.");
                using var fieldBytes = new MemoryStream();
                int fieldBytesRead;
                while ((fieldBytesRead = await part.Body.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (fieldBytes.Length + fieldBytesRead > MultipartLimits.FieldBytes) throw new BadHttpRequestException("Multipart field exceeds 256 bytes.");
                    fieldBytes.Write(buffer, 0, fieldBytesRead);
                }
                fields.Add(partName, Encoding.UTF8.GetString(fieldBytes.ToArray()));
                continue;
            }
            if (files.Count >= options.MaxFiles) throw new BadHttpRequestException(UploadErrors.FileCount);
            if (partName != "files") throw new BadHttpRequestException("File parts must use the field name files.");
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var fileBytes = new MemoryStream((int)Math.Min(options.MaxFileBytes, Math.Min(request.ContentLength ?? MultipartLimits.BufferBytes, MultipartLimits.InitialFileCapacity)));
            var oversized = false;
            long fileLength = 0;
            int fileBytesRead;
            while ((fileBytesRead = await part.Body.ReadAsync(buffer, cancellationToken)) != 0)
            {
                sha256.AppendData(buffer, 0, fileBytesRead);
                fileLength += fileBytesRead;
                if (fileLength > options.MaxFileBytes) oversized = true;
                if (!oversized) fileBytes.Write(buffer, 0, fileBytesRead);
            }
            files.Add(new UploadedFile(SafeFileName(clientFileName), oversized ? null : fileBytes.GetBuffer(), sha256.GetHashAndReset(),
                oversized ? UploadErrors.FileLimit : null, oversized ? null : (int)fileLength));
        }
        if (files.Count == 0) throw new BadHttpRequestException("Upload must contain at least one file.");
        return new MultipartUpload(files, fields);
    }
    private static string SafeFileName(string clientFileName)
    {
        var safeNameBuilder = new StringBuilder();
        foreach (var rune in clientFileName.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            if (safeNameBuilder.Length + rune.Utf16SequenceLength > MultipartLimits.FileNameCharacters) break;
            safeNameBuilder.Append(rune.ToString());
        }
        var safeName = Path.GetFileName(safeNameBuilder.ToString().Replace('\\', '/'));
        return safeName.Length == 0 ? "upload" : safeName;
    }

}

// Counts before buffering; the request owns the underlying stream.
internal sealed class UploadReadStream(Stream inner, long limit) : Stream
{
    private long readBytes;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => readBytes; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = inner.Read(buffer, offset, Allowed(count));
        Count(bytesRead);
        return bytesRead;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken);
        Count(bytesRead);
        return bytesRead;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    private int Allowed(int count) => (int)Math.Min(count, limit - readBytes + 1);
    private void Count(int count)
    {
        readBytes += count;
        if (readBytes > limit) throw new BadHttpRequestException(UploadErrors.TotalLimit, StatusCodes.Status413PayloadTooLarge);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long fieldBytes) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
