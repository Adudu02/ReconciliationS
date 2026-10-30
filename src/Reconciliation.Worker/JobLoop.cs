namespace Reconciliation.Worker;

/// <summary>Placeholder for the job loop. The job-processing tasks (group 4) replace it.</summary>
public sealed partial class JobLoop(ILogger<JobLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            LogStopping(logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker started; the job loop is not implemented yet.")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker stopping.")]
    private static partial void LogStopping(ILogger logger);
}
