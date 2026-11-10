using Reconciliation.Contracts;

namespace Reconciliation.Web.ViewModels;

public sealed class JobProgressViewModel(ApiClient api, TimeProvider clock) : PageViewModel, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public JobResponse? Current { get; private set; }
    public event Action? Changed;
    public async Task WatchAsync(Guid jobId, CancellationToken token = default)
    {
        await RunAsync(() => PollAsync(jobId, token));
        Changed?.Invoke();
    }
    public async Task StartRunAsync(CancellationToken token = default)
    {
        await RunAsync(async () =>
        {
            Current = null;
            var receipt = await api.StartRunAsync(token);
            await PollAsync(receipt.JobId, token);
        });
        Changed?.Invoke();
    }
    private async Task PollAsync(Guid jobId, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var delay = TimeSpan.FromSeconds(1);
        var path = api.CompanyPath($"jobs/{jobId}");
        while (true)
        {
            Current = await api.GetAsync<JobResponse>(path, linked.Token);
            if (Current.State == "failed") Error = new("error.job-failed");
            Changed?.Invoke();
            if (Current.State is "succeeded" or "failed") return;
            await Task.Delay(delay, clock, linked.Token);
            delay = TimeSpan.FromSeconds(Math.Min(5, delay.TotalSeconds * 2));
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
