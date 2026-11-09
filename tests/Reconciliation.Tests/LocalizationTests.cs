using System.Text.RegularExpressions;
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

    private static List<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+\}").Select(match => match.Value).Order().ToList();
}
