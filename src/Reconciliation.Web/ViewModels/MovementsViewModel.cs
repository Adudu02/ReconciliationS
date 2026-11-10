using Reconciliation.Contracts;

namespace Reconciliation.Web.ViewModels;

public sealed class MovementsViewModel(ApiClient api, SessionState session) : PageViewModel
{
    public string State { get; set; } = "";
    public MovementItem[] Movements { get; private set; } = [];
    private MovementPage? page;
    public bool HasMore => page?.NextAfterId is not null;
    public Task LoadAsync() => RunAsync(() => LoadCoreAsync(false));
    public Task NextAsync() => RunAsync(() => LoadCoreAsync(true));
    private async Task LoadCoreAsync(bool next)
    {
        var query = "movements?limit=50" + (State.Length == 0 ? "" : "&state=" + Uri.EscapeDataString(State));
        if (next && page?.NextAfterId is not null) query += $"&afterDate={page.NextAfterDate:yyyy-MM-dd}&afterId={page.NextAfterId}";
        page = await api.GetAsync<MovementPage>(api.CompanyPath(query));
        Movements = next ? [.. Movements, .. page.Movements] : page.Movements;
    }
    public Task ClassifyAsync(Guid movementId, string? classification, string? note) => RunAsync(async () =>
    {
        if (!session.CanWrite) { Error = new("error.read-only"); return; }
        await api.ClassifyAsync(movementId, new(classification, note));
        await LoadCoreAsync(false);
    });
}
