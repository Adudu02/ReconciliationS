using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Contracts;
using Reconciliation.Web;
using Reconciliation.Web.Localization;
using Reconciliation.Web.Pages;
using Reconciliation.Web.Shared;
using TestContext = Xunit.TestContext;

namespace Reconciliation.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void Defaults_to_spanish()
    {
        Assert.Equal("es", new Localizer().Language);
        Assert.Equal(["es", "en"], Localizer.Supported);
    }

    [Fact]
    public void Set_ignores_unknown_languages()
    {
        var localizer = new Localizer();
        var changes = 0;
        localizer.Changed += () => changes++;

        localizer.Set("fr");
        Assert.Equal("es", localizer.Language);
        Assert.Equal(0, changes);

        localizer.Set("en");
        Assert.Equal("en", localizer.Language);
        Assert.Equal(1, changes);

        localizer.Set("en");
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Indexer_falls_back_to_english_then_key()
    {
        var localizer = new Localizer(
            new Dictionary<string, string> { ["only.both"] = "solo es" },
            new Dictionary<string, string> { ["only.both"] = "en only", ["only.en"] = "english text" });

        Assert.Equal("solo es", localizer["only.both"]);
        Assert.Equal("english text", localizer["only.en"]);
        Assert.Equal("missing.key", localizer["missing.key"]);
    }

    [Fact]
    public void Format_substitutes_placeholders()
    {
        var localizer = new Localizer();
        Assert.Equal("3 archivo(s) seleccionado(s)", localizer.Format("import.files-selected", 3));

        localizer.Set("en");
        Assert.Equal("3 file(s) selected", localizer.Format("import.files-selected", 3));
    }

    [Fact]
    public void Catalogs_have_identical_keys_and_placeholders()
    {
        var onlyEs = Catalog.Es.Keys.Except(Catalog.En.Keys).Order().ToList();
        var onlyEn = Catalog.En.Keys.Except(Catalog.Es.Keys).Order().ToList();
        var mismatched = Catalog.Es.Keys.Intersect(Catalog.En.Keys)
            .Where(key => !Placeholders(Catalog.Es[key]).SequenceEqual(Placeholders(Catalog.En[key])))
            .Order().ToList();

        Assert.True(
            onlyEs.Count == 0 && onlyEn.Count == 0 && mismatched.Count == 0,
            $"Catalog parity broken. Only in es: [{string.Join(", ", onlyEs)}]; only in en: [{string.Join(", ", onlyEn)}]; placeholder mismatch: [{string.Join(", ", mismatched)}]");
    }

    [Fact]
    public void No_catalog_value_is_empty()
    {
        var empty = Catalog.Es.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => "es:" + pair.Key)
            .Concat(Catalog.En.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => "en:" + pair.Key))
            .Order().ToList();

        Assert.True(empty.Count == 0, $"Empty catalog values: [{string.Join(", ", empty)}]");
    }

    private static Labels SpanishLabels(out Localizer localizer)
    {
        localizer = new Localizer();
        return new Labels(localizer);
    }

    private static ReviewEntry Entry(string explanation, params ReviewCriterion[] criteria) =>
        new("suggestion", Guid.NewGuid(), Guid.NewGuid(), new InvoiceBrief(DateTime.UnixEpoch, 1m, "AAA010101AAA", "BBB010101BBB"),
            criteria, "high", explanation, []);

    [Fact]
    public void State_translates_known_values()
    {
        var labels = SpanishLabels(out var localizer);
        Assert.Equal("Sugerido", labels.State("suggested"));
        Assert.Equal("Conciliado", labels.State("reconciled"));
        Assert.Equal("Pendiente", labels.State("pending"));

        localizer.Set("en");
        Assert.Equal("Suggested", labels.State("suggested"));
    }

    [Fact]
    public void Every_documented_machine_value_has_a_label_in_both_languages()
    {
        var labels = SpanishLabels(out var localizer);
        foreach (var language in new[] { "es", "en" })
        {
            localizer.Set(language);
            foreach (var v in new[] { "pending", "suggested", "reconciled", "received", "overdue", "missing", "rejected", "cancelled" })
                Assert.NotEqual(v, labels.State(v));
            foreach (var v in new[] { "high", "medium", "low" }) Assert.NotEqual(v, labels.Confidence(v));
            foreach (var v in new[] { "administrator", "accountant", "viewer" }) Assert.NotEqual(v, labels.Role(v));
            foreach (var v in new[] { "issued", "received" }) Assert.NotEqual(v, labels.Direction(v));
            foreach (var v in new[] { "queued", "running", "succeeded", "failed" }) Assert.NotEqual(v, labels.JobState(v));
            foreach (var v in new[] { "received", "overdue", "missing" }) Assert.NotEqual(v, labels.ComplementStatus(v));
            foreach (var v in new[] { "loan", "partner_contribution", "tax_payment", "late_invoicing", "other" })
                Assert.NotEqual(v, labels.Classification(v));
            foreach (var v in new[] { "payment-complement", "identifier-without-amount", "rfc", "invoice-reference", "operation-number",
                "amount-equals-total", "gross-payment", "amount-equals-remaining", "amount-within-tolerance", "date-proximity", "replacement-of-cancelled-invoice" })
                Assert.NotEqual("detail", labels.Criterion(new ReviewCriterion(v, 1, "detail")));
            foreach (var v in new[] { "fully credited", "global invoice", "overpaid", "cancelled credit note", "credit note in a different currency",
                "cancelled invoice", "cancelled payment complement", "complement overdue", "efos warning", "inconsistent balance" })
                Assert.NotEqual(v, labels.Reason(v));
        }
    }

    [Fact]
    public void Null_confidence_and_classification_render_empty()
    {
        var labels = SpanishLabels(out _);
        Assert.Equal("", labels.Confidence(null));
        Assert.Equal("", labels.Classification(null));
        Assert.Equal("Alta", labels.Confidence("high"));
        Assert.Equal("Préstamo", labels.Classification("loan"));
    }

    [Fact]
    public void Unknown_values_pass_through_raw()
    {
        var labels = SpanishLabels(out _);
        Assert.Equal("quarantined", labels.State("quarantined"));
        Assert.Equal("brand_new_code", labels.Reason("brand_new_code"));
        Assert.Equal("auditor", labels.Role("auditor"));
        Assert.Equal("weird", labels.Confidence("weird"));
        Assert.Equal("free text", labels.Classification("free text"));
    }

    [Fact]
    public void Labels_follow_the_language_at_call_time()
    {
        var labels = SpanishLabels(out var localizer);
        Assert.Equal("Administrador", labels.Role("administrator"));
        localizer.Set("en");
        Assert.Equal("Administrator", labels.Role("administrator"));
        localizer.Set("es");
        Assert.Equal("Administrador", labels.Role("administrator"));
    }

    [Fact]
    public void Criterion_uses_code_and_ignores_server_detail()
    {
        var labels = SpanishLabels(out _);
        Assert.Equal("la serie/folio de la factura o un fragmento del UUID aparece en el texto",
            labels.Criterion(new ReviewCriterion("invoice-reference", 30, "any english")));
        Assert.Equal("el monto es igual al saldo pendiente de pago",
            labels.Criterion(new ReviewCriterion("amount-equals-remaining", 30, "other english")));
        Assert.Equal("some server detail", labels.Criterion(new ReviewCriterion("future-code", 5, "some server detail")));
        Assert.Equal("el monto está dentro de la tolerancia respecto al monto esperado",
            labels.Criterion(new ReviewCriterion("amount-within-tolerance", 25, "unrecognised english detail")));
    }

    [Fact]
    public void Explanation_rebuilds_from_criteria_and_keeps_counts()
    {
        var labels = SpanishLabels(out var localizer);
        var invoiceReference = new ReviewCriterion("invoice-reference", 30, "invoice serie/folio or UUID fragment found in text");
        var remaining = new ReviewCriterion("amount-equals-remaining", 30, "amount equals remaining payable");

        var equal = Entry("invoice serie/folio or UUID fragment found in text; amount equals remaining payable; 3 other invoices matched equally", invoiceReference, remaining);
        Assert.Equal("la serie/folio de la factura o un fragmento del UUID aparece en el texto; el monto es igual al saldo pendiente de pago; 3 facturas más coincidieron por igual",
            labels.Explanation(equal));

        var capped = Entry("invoice serie/folio or UUID fragment found in text; amount equals remaining payable; 50+ others may have matched equally", invoiceReference, remaining);
        Assert.Equal("la serie/folio de la factura o un fragmento del UUID aparece en el texto; el monto es igual al saldo pendiente de pago; 50+ facturas más podrían haber coincidido por igual",
            labels.Explanation(capped));

        var plain = Entry("invoice serie/folio or UUID fragment found in text; amount equals remaining payable", invoiceReference, remaining);
        Assert.Equal("la serie/folio de la factura o un fragmento del UUID aparece en el texto; el monto es igual al saldo pendiente de pago", labels.Explanation(plain));

        localizer.Set("en");
        Assert.Equal("invoice serie/folio or UUID fragment found in text; amount equals remaining payable; 3 other invoices matched equally", labels.Explanation(equal));
    }

    [Fact]
    public void Explanation_returns_server_text_when_shape_is_unknown()
    {
        var labels = SpanishLabels(out _);
        var criterion = new ReviewCriterion("invoice-reference", 30, "invoice serie/folio or UUID fragment found in text");

        var closed = Entry("invoice serie/folio or UUID fragment found in text; invoice cancelled by SAT", criterion);
        Assert.Equal(closed.Explanation, labels.Explanation(closed));

        var noCriteria = Entry("Invoice requires review");
        Assert.Equal("Invoice requires review", labels.Explanation(noCriteria));

        var mismatch = Entry("something else; 2 other invoices matched equally", criterion);
        Assert.Equal(mismatch.Explanation, labels.Explanation(mismatch));
    }

    [Fact]
    public void Criterion_keeps_evidence_from_known_server_details_in_both_languages()
    {
        var labels = SpanishLabels(out var localizer);
        var credit = new ReviewCriterion("amount-equals-total", 40, "amount equals net payable after credit notes");
        var plainTotal = new ReviewCriterion("amount-equals-total", 40, "amount equals CFDI net total");
        var tolerance = new ReviewCriterion("amount-within-tolerance", 25, "amount within tolerance of remaining payable target; difference 50 cents");
        var gross = new ReviewCriterion("amount-within-tolerance", 25, "amount within tolerance of gross (amount equals total before retentions) target; difference 0.5 cents");
        var date = new ReviewCriterion("date-proximity", 25, "invoice date is within tolerance; proximity bonus 5");

        Assert.Equal("el monto es igual al neto a pagar después de notas de crédito", labels.Criterion(credit));
        Assert.Equal("el monto es igual al total neto del CFDI", labels.Criterion(plainTotal));
        Assert.Equal("el monto está dentro de la tolerancia del objetivo saldo pendiente de pago; diferencia de 50 centavos", labels.Criterion(tolerance));
        Assert.Equal("el monto está dentro de la tolerancia del objetivo bruto (monto igual al total antes de retenciones); diferencia de 0.5 centavos", labels.Criterion(gross));
        Assert.Equal("la fecha de la factura está dentro de la tolerancia; bonificación por cercanía de 5", labels.Criterion(date));

        localizer.Set("en");
        Assert.Equal("amount equals net payable after credit notes", labels.Criterion(credit));
        Assert.Equal("amount equals CFDI net total", labels.Criterion(plainTotal));
        Assert.Equal("amount within tolerance of remaining payable target; difference 50 cents", labels.Criterion(tolerance));
        Assert.Equal("amount within tolerance of gross (amount equals total before retentions) target; difference 0.5 cents", labels.Criterion(gross));
        Assert.Equal("invoice date is within tolerance; proximity bonus 5", labels.Criterion(date));
    }

    [Fact]
    public void Criterion_with_unrecognised_detail_falls_back_to_the_static_label()
    {
        var labels = SpanishLabels(out _);
        Assert.Equal("el monto está dentro de la tolerancia respecto al monto esperado",
            labels.Criterion(new ReviewCriterion("amount-within-tolerance", 25, "something new from the server")));
        Assert.Equal("el monto está dentro de la tolerancia respecto al monto esperado",
            labels.Criterion(new ReviewCriterion("amount-within-tolerance", 25, "amount within tolerance of mystery target; difference 3 cents")));
        Assert.Equal("la fecha de la factura está dentro de la tolerancia",
            labels.Criterion(new ReviewCriterion("date-proximity", 25, "date is close")));
        Assert.Equal("el monto es igual al total neto del CFDI",
            labels.Criterion(new ReviewCriterion("amount-equals-total", 40, "unexpected wording")));
    }

    [Fact]
    public void Explanation_with_date_proximity_is_fully_spanish()
    {
        var labels = SpanishLabels(out _);
        var criteria = new[]
        {
            new ReviewCriterion("amount-within-tolerance", 25, "amount within tolerance of total target; difference 1 cents"),
            new ReviewCriterion("date-proximity", 28, "invoice date is within tolerance; proximity bonus 8"),
        };
        var entry = Entry("amount within tolerance of total target; difference 1 cents; invoice date is within tolerance; proximity bonus 8; 2 other invoices matched equally", criteria);

        Assert.Equal("el monto está dentro de la tolerancia del objetivo total; diferencia de 1 centavos; la fecha de la factura está dentro de la tolerancia; bonificación por cercanía de 8; 2 facturas más coincidieron por igual",
            labels.Explanation(entry));
    }

    private static List<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+\}").Select(match => match.Value).Order().ToList();
}

public sealed class LanguageSwitchTests
{
    private static readonly CompanyItem Company = new(Guid.NewGuid(), "AAA010101AAA", "Synthetic company", null, "accountant");

    private static (SessionState Session, ApiClient Api) Services(BunitContext context, string language)
    {
        TestLanguage.Register(context, language);
        var session = new SessionState();
        var http = new HttpClient(new WebTestHandler(request => request.RequestUri!.AbsolutePath == "/auth/tokens"
            ? WebTestHandler.Json(new SessionTokens("synthetic-access", 900))
            : new(HttpStatusCode.Unauthorized)))
        { BaseAddress = new Uri("https://app.example.invalid/") };
        var api = new ApiClient(http, session);
        context.Services.AddSingleton(session);
        context.Services.AddSingleton(api);
        return (session, api);
    }

    private static async Task<SessionState> Authenticated(BunitContext context, string language)
    {
        var (session, api) = Services(context, language);
        await api.LoginAsync("synthetic@example.invalid", "synthetic-password", TestContext.Current.CancellationToken);
        session.SelectCompany(Company);
        // An unknown route renders the shell's NotFound content, so no page (and none of its services) is needed.
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/ui/missing");
        return session;
    }

    private static bool HasSubscribers(Localizer localizer) =>
        typeof(Localizer).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(localizer) is not null;

    [Fact]
    public async Task Login_renders_in_spanish_by_default()
    {
        await using var context = new BunitContext();
        Services(context, "es");
        var page = context.Render<Login>();
        Assert.Contains("Iniciar sesión", page.Markup, StringComparison.Ordinal);
        Assert.Contains("los resultados no tienen validez fiscal ni legal", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Correo electrónico", page.Markup, StringComparison.Ordinal);
        Assert.Contains("Contraseña", page.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switch_to_english_rerenders_and_sets_document_language()
    {
        await using var context = new BunitContext();
        Services(context, "es");
        var document = context.Services.GetRequiredService<RecordingDocumentLanguage>();
        var app = context.Render<App>();
        app.WaitForAssertion(() => Assert.Contains("Iniciar sesión", app.Markup, StringComparison.Ordinal));
        Assert.Equal("true", app.Find("button[lang=es]").GetAttribute("aria-pressed"));
        Assert.Equal("false", app.Find("button[lang=en]").GetAttribute("aria-pressed"));
        Assert.Equal(["es"], document.Languages);

        app.Find("button[lang=en]").Click();

        app.WaitForAssertion(() => Assert.Contains("Sign in", app.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Iniciar sesión", app.Markup, StringComparison.Ordinal);
        Assert.Contains("Demo: results have no fiscal or legal validity.", app.Markup, StringComparison.Ordinal);
        Assert.Equal("false", app.Find("button[lang=es]").GetAttribute("aria-pressed"));
        Assert.Equal("true", app.Find("button[lang=en]").GetAttribute("aria-pressed"));
        Assert.Equal(["es", "en"], document.Languages);
    }

    [Fact]
    public async Task Switch_renders_two_labelled_buttons_that_name_their_own_language()
    {
        await using var context = new BunitContext();
        Services(context, "es");
        var page = context.Render<LanguageSwitch>();
        Assert.Equal("group", page.Find(".lang-switch").GetAttribute("role"));
        Assert.Equal("Idioma", page.Find(".lang-switch").GetAttribute("aria-label"));
        var buttons = page.FindAll(".lang-switch button");
        Assert.Equal(["ES", "EN"], buttons.Select(button => button.TextContent.Trim()));
        Assert.Equal(["es", "en"], buttons.Select(button => button.GetAttribute("lang")));
    }

    [Theory]
    [InlineData("/ui/login?lang=en", "en", "Sign in")]
    [InlineData("/ui/login?lang=es", "es", "Iniciar sesión")]
    [InlineData("/ui/login?lang=zz", "es", "Iniciar sesión")]
    [InlineData("/ui/login", "es", "Iniciar sesión")]
    public async Task Query_parameter_selects_initial_language(string uri, string expected, string text)
    {
        await using var context = new BunitContext();
        Services(context, "en");
        var document = context.Services.GetRequiredService<RecordingDocumentLanguage>();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        var app = context.Render<App>();
        app.WaitForAssertion(() => Assert.Contains(text, app.Markup, StringComparison.Ordinal));
        Assert.Equal(expected, context.Services.GetRequiredService<Localizer>().Language);
        Assert.Equal(expected, document.Languages[0]);
    }

    [Fact]
    public async Task Switching_language_keeps_the_selected_company()
    {
        await using var context = new BunitContext();
        var session = await Authenticated(context, "es");
        var app = context.Render<App>();
        app.WaitForAssertion(() => Assert.Contains("Cambiar de empresa", app.Markup, StringComparison.Ordinal));
        Assert.Contains("Contador", app.Find(".identity").TextContent, StringComparison.Ordinal);

        app.Find("button[lang=en]").Click();

        app.WaitForAssertion(() => Assert.Contains("Switch company", app.Markup, StringComparison.Ordinal));
        Assert.Contains("Accountant", app.Find(".identity").TextContent, StringComparison.Ordinal);
        Assert.Equal(Company, session.Company);
        Assert.Contains("Synthetic company", app.Find(".identity").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Archived_company_is_labelled_in_the_active_language()
    {
        await using var context = new BunitContext();
        var session = await Authenticated(context, "es");
        session.SelectCompany(Company with { ArchivedAt = DateTime.UtcNow });
        var app = context.Render<App>();
        app.WaitForAssertion(() => Assert.Contains("Archivada · solo lectura", app.Find(".identity").TextContent, StringComparison.Ordinal));
        app.Find("button[lang=en]").Click();
        app.WaitForAssertion(() => Assert.Contains("Archived · read-only", app.Find(".identity").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shell_chrome_is_localized_including_accessible_names()
    {
        await using var context = new BunitContext();
        await Authenticated(context, "es");
        var app = context.Render<App>();
        app.WaitForAssertion(() => Assert.Contains("Cambiar de empresa", app.Markup, StringComparison.Ordinal));
        Assert.Equal("Saltar al contenido", app.Find("a.skip").TextContent);
        Assert.Equal("Espacio de trabajo", app.Find("nav.sidebar").GetAttribute("aria-label"));
        Assert.Equal(["Resumen", "Bandeja de revisión", "Facturas", "Movimientos", "Importar archivos"],
            app.FindAll("nav.sidebar a").Select(link => link.TextContent.Trim()));
        Assert.Contains("Demostración: los resultados no tienen validez fiscal ni legal.", app.Find("footer").TextContent, StringComparison.Ordinal);
        Assert.Contains("Página no disponible", app.Find("main").TextContent, StringComparison.Ordinal);
        Assert.Contains("Volver al resumen", app.Find("main").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Components_release_the_language_subscription_when_disposed()
    {
        await using var context = new BunitContext();
        Services(context, "es");
        var localizer = context.Services.GetRequiredService<Localizer>();
        Assert.False(HasSubscribers(localizer));
        var app = context.Render<App>();
        app.WaitForAssertion(() => Assert.Contains("Iniciar sesión", app.Markup, StringComparison.Ordinal));
        Assert.True(HasSubscribers(localizer));

        await context.DisposeComponentsAsync();

        Assert.False(HasSubscribers(localizer));
    }
}
