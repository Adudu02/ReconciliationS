using System.Text.Json;

namespace Reconciliation.Web.ViewModels;

public abstract class PageViewModel
{
    public string? Error { get; protected set; }
    public bool Busy { get; private set; }
    protected async Task RunAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        Error = null;
        try { await action(); }
        catch (ApiProblemException problem) { Error = problem.Message; }
        catch (HttpRequestException) { Error = "The service is unavailable. Try again."; }
        catch (JsonException) { Error = "The API response could not be read. Refresh the page."; }
        catch (OperationCanceledException) { Error = "The request was cancelled or timed out. Refresh to check the current state."; }
        finally { Busy = false; }
    }
}
