using System.Text.Json;
using Reconciliation.Web;
using Reconciliation.Web.ViewModels;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class WebSchemaDetailsTests(ApiFixture fixture)
{
    [Fact]
    public async Task Multipart_files_are_documented_as_binary_and_review_criteria_have_their_real_fields()
    {
        using var client = fixture.App.CreateClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi.json", TestContext.Current.CancellationToken));
        var root = json.RootElement;
        var paths = root.GetProperty("paths");
        var upload = Resolve(root, paths.GetProperty("/companies/{companyId}/imports/cfdi").GetProperty("post").GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema"));
        var file = Resolve(root, upload.GetProperty("properties").GetProperty("files").GetProperty("items"));
        Assert.Equal("string", file.GetProperty("type").GetString());
        Assert.Equal("binary", file.GetProperty("format").GetString());
        var page = Resolve(root, paths.GetProperty("/companies/{companyId}/review-queue").GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema"));
        var entry = Resolve(root, page.GetProperty("properties").GetProperty("entries").GetProperty("items"));
        var criterion = Resolve(root, entry.GetProperty("properties").GetProperty("criteria").GetProperty("items"));
        Assert.True(criterion.TryGetProperty("properties", out var fields), "Criteria must describe code, points and detail, rather than an untyped JSON value.");
        Assert.True(fields.TryGetProperty("code", out _));
        Assert.True(fields.TryGetProperty("points", out _));
        Assert.True(fields.TryGetProperty("detail", out _));
    }

    private static JsonElement Resolve(JsonElement root, JsonElement schema)
    {
        for (var depth = 0; depth < 20 && schema.TryGetProperty("$ref", out var reference); depth++)
            schema = root.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/')[^1]);
        return schema;
    }
}

public sealed class WebTimeoutTests
{
    [Fact]
    public async Task A_timed_out_request_has_a_readable_error_instead_of_silent_stale_data()
    {
        using var http = new HttpClient(new WebTestHandler(_ => throw new TaskCanceledException("Synthetic raw timeout"))) { BaseAddress = new Uri("https://app.example.invalid/") };
        using var api = new ApiClient(http, new SessionState());
        var model = new WorkspaceViewModel(api);
        await model.LoadCompaniesAsync();
        Assert.NotNull(model.Error);
        Assert.DoesNotContain("Synthetic raw timeout", model.Error, StringComparison.Ordinal);
    }
}
