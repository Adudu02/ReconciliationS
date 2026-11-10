using System.Globalization;

namespace Reconciliation.Web.Localization;

/// <summary>A translatable error: a catalog key plus optional raw server detail and HTTP status.</summary>
public sealed record UiError(string Key, string? Raw = null, int? Status = null);

/// <summary>Scoped per-session language state. Looks keys up in the active catalog, then English, then the key itself.</summary>
public sealed class Localizer
{
    private readonly IReadOnlyDictionary<string, string> _es;
    private readonly IReadOnlyDictionary<string, string> _en;

    public Localizer() : this(Catalog.Es, Catalog.En) { }

    public Localizer(IReadOnlyDictionary<string, string> es, IReadOnlyDictionary<string, string> en)
    {
        _es = es;
        _en = en;
    }

    public static IReadOnlyList<string> Supported { get; } = ["es", "en"];

    public string Language { get; private set; } = "es";

    public event Action? Changed;

    public void Set(string language)
    {
        if (!Supported.Contains(language) || language == Language) return;
        Language = language;
        Changed?.Invoke();
    }

    public string this[string key]
    {
        get
        {
            var active = Language == "en" ? _en : _es;
            if (active.TryGetValue(key, out var text)) return text;
            return _en.TryGetValue(key, out var fallback) ? fallback : key;
        }
    }

    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, this[key], args);
}
