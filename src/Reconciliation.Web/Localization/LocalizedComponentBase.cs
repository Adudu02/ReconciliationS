using Microsoft.AspNetCore.Components;

namespace Reconciliation.Web.Localization;

/// <summary>
/// Base for components that show catalog text. Exposes <see cref="T"/> (keyed text) and <see cref="L"/> (machine-value labels)
/// and re-renders in place when the language changes, so child state such as open forms is never recreated.
/// Subclasses that own resources override <see cref="Dispose(bool)"/> and call the base.
/// </summary>
public abstract class LocalizedComponentBase : ComponentBase, IDisposable
{
    private bool subscribed;

    [Inject] protected Localizer T { get; set; } = default!;

    [Inject] protected Labels L { get; set; } = default!;

    // Server-authored sentences are English. In Spanish they are marked lang="en" so assistive technology pronounces them correctly; in English no marker is needed.
    protected string? ServerLang => T.Language == "en" ? null : "en";

    public override Task SetParametersAsync(ParameterView parameters)
    {
        if (!subscribed)
        {
            T.Changed += OnLanguageChanged;
            subscribed = true;
        }
        return base.SetParametersAsync(parameters);
    }

    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing || !subscribed) return;
        T.Changed -= OnLanguageChanged;
        subscribed = false;
    }
}
