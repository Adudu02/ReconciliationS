using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Reconciliation.Core.Ingestion;

internal sealed record MultipartUpload(IReadOnlyList<UploadedFile> Files, IReadOnlyDictionary<string, string> Fields)
{
    public static async Task<MultipartUpload> ReadAsync(HttpRequest request, IngestionOptions options, CancellationToken ct)
    {
        var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = options.MaxUploadBytes;
        if (request.ContentLength > options.MaxUploadBytes) throw new BadHttpRequestException("Upload exceeds MaxUploadBytes.", StatusCodes.Status413PayloadTooLarge);
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media) || !string.Equals(media.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Expected multipart/form-data.", StatusCodes.Status415UnsupportedMediaType);
        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new BadHttpRequestException("Invalid multipart boundary.");
        using var bounded = new UploadReadStream(request.Body, options.MaxUploadBytes);
        var reader = new MultipartReader(boundary, bounded);
        var files = new List<UploadedFile>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var buffer = new byte[8192];
        while (await reader.ReadNextSectionAsync(ct) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                throw new BadHttpRequestException("Invalid multipart content disposition.");
            var field = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
            var filename = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            if (filename is null)
            {
                if (fields.Count >= 8 || fields.ContainsKey(field)) throw new BadHttpRequestException("Too many or duplicate multipart fields.");
                using var value = new MemoryStream();
                int fieldRead;
                while ((fieldRead = await section.Body.ReadAsync(buffer, ct)) != 0)
                {
                    if (value.Length + fieldRead > 256) throw new BadHttpRequestException("Multipart field exceeds 256 bytes.");
                    value.Write(buffer, 0, fieldRead);
                }
                fields.Add(field, Encoding.UTF8.GetString(value.ToArray()));
                continue;
            }
            if (files.Count >= options.MaxFiles) throw new BadHttpRequestException("Upload exceeds MaxFiles.");
            if (field != "files") throw new BadHttpRequestException("File parts must use the field name files.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var content = new MemoryStream();
            var oversized = false;
            long length = 0;
            int read;
            while ((read = await section.Body.ReadAsync(buffer, ct)) != 0)
            {
                hash.AppendData(buffer, 0, read);
                length += read;
                if (length > options.MaxFileBytes) oversized = true;
                if (!oversized) content.Write(buffer, 0, read);
            }
            files.Add(new UploadedFile(Path.GetFileName(filename.Replace('\\', '/')), oversized ? null : content.ToArray(), hash.GetHashAndReset(),
                oversized ? "File exceeds MaxFileBytes." : null));
        }
        if (files.Count == 0) throw new BadHttpRequestException("Upload must contain at least one file.");
        return new MultipartUpload(files, fields);
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
        var read = inner.Read(buffer, offset, Allowed(count));
        Count(read);
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer[..Allowed(buffer.Length)], cancellationToken);
        Count(read);
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    private int Allowed(int count) => (int)Math.Min(count, limit - readBytes + 1);
    private void Count(int count)
    {
        readBytes += count;
        if (readBytes > limit) throw new BadHttpRequestException("Upload exceeds MaxUploadBytes.", StatusCodes.Status413PayloadTooLarge);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
