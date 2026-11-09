using System.Text.Json;
using Reconciliation.Web.Localization;

namespace Reconciliation.Web.ViewModels;

public abstract class PageViewModel
{
    public UiError? Error { get; protected set; }
    public bool Busy { get; private set; }
    protected async Task RunAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true;
        Error = null;
        try { await action(); }
        catch (ApiProblemException problem) { Error = problem.ToUiError(); }
        catch (HttpRequestException) { Error = new("error.service-unavailable"); }
        catch (JsonException) { Error = new("error.unreadable-response"); }
        catch (OperationCanceledException) { Error = new("error.cancelled"); }
        finally { Busy = false; }
    }
}
