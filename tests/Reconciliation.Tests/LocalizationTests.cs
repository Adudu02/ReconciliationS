using System.Text.RegularExpressions;
using Reconciliation.Contracts;
using Reconciliation.Web.Localization;

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
                "amount-equals-total", "gross-payment", "amount-equals-remaining", "amount-within-tolerance", "replacement-of-cancelled-invoice" })
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
        Assert.Contains("; 50+ ", labels.Explanation(capped));

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

    private static List<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+\}").Select(match => match.Value).Order().ToList();
}
