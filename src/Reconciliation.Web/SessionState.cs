using Reconciliation.Contracts;

namespace Reconciliation.Web;

public sealed class SessionState
{
    internal string? AccessToken { get; private set; }
    public bool IsAuthenticated => AccessToken is not null;
    public bool PasswordChangeCompleted { get; private set; }
    public CompanyItem? Company { get; private set; }
    public bool CanWrite => Company is { ArchivedAt: null, Role: "administrator" or "accountant" };
    public event Action? Changed;
    internal void Authenticate(SessionTokens tokens) { AccessToken = tokens.AccessToken; Changed?.Invoke(); }
    public void SwitchCompany() { Company = null; Changed?.Invoke(); }
    public void SelectCompany(CompanyItem company) { Company = company; Changed?.Invoke(); }
    internal void Clear(bool passwordChanged = false) { AccessToken = null; Company = null; PasswordChangeCompleted = passwordChanged; Changed?.Invoke(); }
}
