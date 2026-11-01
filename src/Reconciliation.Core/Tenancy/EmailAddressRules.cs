using System.Net.Mail;

namespace Reconciliation.Core.Tenancy;

public static class EmailAddressRules
{
    public static bool IsValid(string? email) => MailAddress.TryCreate(email, out var parsedEmail) && parsedEmail.Address == email;
}
