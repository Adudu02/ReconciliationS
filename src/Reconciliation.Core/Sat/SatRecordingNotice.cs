using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Reconciliation.Core.Sat;

internal sealed partial class SatRecordingNotice(SatOptions options, ILogger<SatRecordingNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Mode == "real" && options.RecordResponses) RecordingEnabled(logger);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "SAT response recording is enabled for a real endpoint; keep recordings in .local, outside version control.")]
    private static partial void RecordingEnabled(ILogger logger);
}
