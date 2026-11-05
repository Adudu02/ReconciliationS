using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Reconciliation.Core.Sat;

internal sealed record SatRecording(string CodeRaw, string StateRaw, string CancellabilityRaw, string CancellationStatusRaw, string? EfosRaw, DateTimeOffset CheckedAt);

public sealed partial class RecordingSatClient(ISatClient inner, string directory, TimeProvider clock, ILogger logger) : ISatClient
{
    private static int warned;
    private long skipped;
    public long SkippedRecordings => Interlocked.Read(ref skipped);

    public async Task<SatStatusResponse> CheckAsync(SatStatusRequest request, CancellationToken cancellationToken)
    {
        var response = await inner.CheckAsync(request, cancellationToken);
        try { await RecordAsync(request, response, cancellationToken); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref skipped);
            if (Interlocked.Exchange(ref warned, 1) == 0) Skipped(logger);
        }
        return response;
    }

    public async Task RecordAsync(SatStatusRequest request, SatStatusResponse response, CancellationToken cancellationToken)
    {
        if (request.AnonymizedUuid is null || request.AnonymizedUuid == Guid.Empty || request.AnonymizedUuid == request.Uuid)
            throw new ArgumentException("Recording requires a different nonempty anonymised UUID.", nameof(request));
        var reply = response.Reply;
        var fields = new[] { reply.Code.Raw, reply.State.Raw, reply.Cancellability.Raw, reply.CancellationStatus.Raw, reply.EfosRaw };
        var secrets = new[] { request.Uuid.ToString("D"), request.Uuid.ToString("N"), request.IssuerRfc, request.ReceiverRfc, SatContract.FormatTotal(request.Total) };
        if (fields.Any(field => field is not null && secrets.Any(secret => !string.IsNullOrEmpty(secret) && field.Contains(secret, StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Recording response failed the privacy guard.", nameof(response));
        var recording = new SatRecording(reply.Code.Raw, reply.State.Raw, reply.Cancellability.Raw, reply.CancellationStatus.Raw, reply.EfosRaw, clock.GetUtcNow());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, request.AnonymizedUuid.Value.ToString("D") + ".json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(recording, JsonSerializerOptions.Web), cancellationToken);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "recording skipped: no anonymised key")]
    private static partial void Skipped(ILogger logger);
}

public sealed class ReplaySatClient(string directory) : ISatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web) { MaxDepth = 8 };
    public async Task<SatStatusResponse> CheckAsync(SatStatusRequest request, CancellationToken cancellationToken)
    {
        var key = request.AnonymizedUuid ?? request.Uuid;
        var path = Path.Combine(directory, key.ToString("D") + ".json");
        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length > 512 * 1024) throw new JsonException("Recording size limit.");
            var recording = await JsonSerializer.DeserializeAsync<SatRecording>(stream, JsonOptions, cancellationToken);
            if (recording is null || recording.CodeRaw is null || recording.StateRaw is null || recording.CancellabilityRaw is null || recording.CancellationStatusRaw is null)
                throw new JsonException("Recording fields missing.");
            return SatContract.FromFields(recording.CodeRaw, recording.StateRaw, recording.CancellabilityRaw, recording.CancellationStatusRaw, recording.EfosRaw);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { throw new SatTransientException("SAT unavailable: no recording for key or recording is invalid.", error); }
    }
}
