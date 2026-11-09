using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Reconciliation.Web.Localization;

namespace Reconciliation.Tests;

/// <summary>Registers the localization services a component under test needs. Defaults to English so existing English assertions keep proving the English text.</summary>
internal static class TestLanguage
{
    public static void Register(BunitContext context, string language = "en")
    {
        var localizer = new Localizer();
        localizer.Set(language);
        var document = new RecordingDocumentLanguage();
        context.Services.AddSingleton(localizer);
        context.Services.AddSingleton(new Labels(localizer));
        context.Services.AddSingleton(document);
        context.Services.AddSingleton<IDocumentLanguage>(document);
    }
}

internal sealed class RecordingDocumentLanguage : IDocumentLanguage
{
    public List<string> Languages { get; } = [];

    public ValueTask SetAsync(string language)
    {
        Languages.Add(language);
        return ValueTask.CompletedTask;
    }
}
