namespace Reconciliation.Core.Jobs;

public sealed class JobOptions
{
    public const string SectionName = "Jobs";
    public int PollIntervalMilliseconds { get; set; } = 500;
    public int Concurrency { get; set; } = 2;
    public int LeaseSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 5;
    public int BackoffBaseSeconds { get; set; } = 2;
    public int BackoffCapSeconds { get; set; } = 300;

    public void Validate()
    {
        if (PollIntervalMilliseconds is < 1 or > 60000 || Concurrency is < 1 or > 32 || LeaseSeconds is < 1 or > 86400 ||
            MaxAttempts is < 1 or > 100 || BackoffBaseSeconds < 1 || BackoffCapSeconds < BackoffBaseSeconds || BackoffCapSeconds > 86400)
            throw new InvalidOperationException("Jobs: invalid poll interval, concurrency, lease, attempts or backoff bounds.");
    }
}

public sealed class JobBackoff(JobOptions options, Func<int, int> jitter)
{
    public TimeSpan Delay(int attempt)
    {
        var seconds = (int)Math.Min(options.BackoffCapSeconds, options.BackoffBaseSeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 30)));
        return TimeSpan.FromSeconds(Math.Min(options.BackoffCapSeconds, seconds + jitter(Math.Max(1, seconds / 5 + 1))));
    }
}
