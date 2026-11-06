using Reconciliation.Contracts;

namespace Reconciliation.Web.ViewModels;

public sealed class WorkspaceViewModel(ApiClient api) : PageViewModel
{
    public CompanyItem[] Companies { get; private set; } = [];
    public OverviewResponse? Overview { get; private set; }
    public Task LoadCompaniesAsync() => RunAsync(async () => Companies = await api.GetAsync<CompanyItem[]>("companies"));
    public Task LoadOverviewAsync() => RunAsync(async () => Overview = await api.GetAsync<OverviewResponse>(api.CompanyPath("overview")));
}
