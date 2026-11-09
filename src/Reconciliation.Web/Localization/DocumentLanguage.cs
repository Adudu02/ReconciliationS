using Microsoft.JSInterop;

namespace Reconciliation.Web.Localization;

/// <summary>Reflects the active UI language on the document element so assistive technology and spell-check follow it.</summary>
public interface IDocumentLanguage
{
    ValueTask SetAsync(string language);
}

/// <summary>Calls <c>window.setLanguage</c> from the same-origin <c>lang.js</c>; no inline script is involved.</summary>
public sealed class DocumentLanguage(IJSRuntime js) : IDocumentLanguage
{
    public ValueTask SetAsync(string language) => js.InvokeVoidAsync("setLanguage", language);
}
