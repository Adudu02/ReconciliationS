using Reconciliation.Contracts;

namespace Reconciliation.Web.ViewModels;

public sealed class TeamViewModel(ApiClient api, SessionState session) : PageViewModel
{
    public MemberItem[] Members { get; private set; } = [];
    public bool CanManage => session.Company is { ArchivedAt: null, Role: "administrator" };
    public Task LoadAsync() => RunAsync(ReadAsync);
    public Task AddAsync(string email, string role, string initialPassword) => RunAsync(async () =>
    {
        if (!CanManage) { Error = new("error.read-only"); return; }
        if (Members.Any(member => string.Equals(member.Email, email, StringComparison.OrdinalIgnoreCase)))
        { Error = new("team.already-member"); return; }
        if (initialPassword.Length is > 0 and < 12) { Error = new("team.password-length"); return; }
        await api.AddMemberAsync(new(role, Email: email, InitialPassword: initialPassword.Length == 0 ? null : initialPassword));
        await ReadAsync();
    });
    private async Task ReadAsync() => Members = await api.GetAsync<MemberItem[]>(api.CompanyPath("members"));
}
