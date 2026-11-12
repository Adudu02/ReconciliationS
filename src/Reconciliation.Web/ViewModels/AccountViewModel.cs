namespace Reconciliation.Web.ViewModels;

public sealed class AccountViewModel(ApiClient api) : PageViewModel
{
    public Task ChangeAsync(string currentPassword, string newPassword, string confirmation) => RunAsync(async () =>
    {
        if (newPassword.Length < 12) { Error = new("account.password-length"); return; }
        if (newPassword != confirmation) { Error = new("account.password-mismatch"); return; }
        await api.ChangePasswordAsync(currentPassword, newPassword);
    });
}
