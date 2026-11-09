namespace Reconciliation.Web.Localization;

public static partial class Catalog
{
    public static IReadOnlyDictionary<string, string> En { get; } = new Dictionary<string, string>
    {
        ["nav.overview"] = "Overview",
        ["nav.review"] = "Review queue",
        ["nav.invoices"] = "Invoices",
        ["nav.movements"] = "Movements",
        ["nav.imports"] = "Import files",

        ["topbar.switch-company"] = "Switch company",
        ["topbar.sign-out"] = "Sign out",
        ["topbar.demo"] = "Demo",

        ["login.title"] = "Sign in",
        ["login.eyebrow"] = "Access",
        ["login.intro"] = "Sign in with your email and password to review your company's reconciliation.",
        ["login.email"] = "Email",
        ["login.password"] = "Password",
        ["login.sign-in"] = "Sign in",
        ["login.signing-in"] = "Signing in…",
        ["login.service-unavailable"] = "The service is unavailable right now. Try again in a few minutes.",

        ["notice.disclaimer"] = "Demo: results have no fiscal or legal validity.",

        ["import.files-selected"] = "{0} file(s) selected",
    };
}
