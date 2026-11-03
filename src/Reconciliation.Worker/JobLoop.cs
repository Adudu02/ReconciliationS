using Npgsql;
using Reconciliation.Core.Jobs;

namespace Reconciliation.Worker;

public sealed partial class JobLoop(JobDispatcher dispatcher, JobOptions options, TimeProvider clock, ILogger<JobLoop> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, options.Concurrency).Select(lane => PollAsync(lane, stoppingToken)));

    private async Task PollAsync(int lane, CancellationToken stoppingToken)
    {
        var workerId = $"{Environment.MachineName}-{Guid.NewGuid():N}-{lane}";
        Started(logger, workerId);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { if (await dispatcher.RunOneAsync(workerId, stoppingToken)) continue; }
                catch (NpgsqlException) { DatabaseUnavailable(logger, workerId); }
                await Task.Delay(TimeSpan.FromMilliseconds(options.PollIntervalMilliseconds), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { Stopping(logger, workerId); }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker polling lane started {WorkerId}")]
    private static partial void Started(ILogger logger, string workerId);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Queue database temporarily unavailable for {WorkerId}")]
    private static partial void DatabaseUnavailable(ILogger logger, string workerId);
    [LoggerMessage(Level = LogLevel.Information, Message = "Worker polling lane stopping {WorkerId}")]
    private static partial void Stopping(ILogger logger, string workerId);
}
