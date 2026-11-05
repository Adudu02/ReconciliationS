using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reconciliation.Core.Jobs;
using Reconciliation.Core.Sat;
using Reconciliation.Core.Tenancy;

namespace Reconciliation.Tests;

public sealed class SatConfigurationTests
{
    [Theory]
    [InlineData("Mode", "other")]
    [InlineData("Mode", "")]
    [InlineData("Endpoint", "relative/path")]
    [InlineData("Endpoint", "ftp://example.invalid/")]
    [InlineData("Endpoint", "https://user:password@example.invalid/")]
    [InlineData("CurrentCacheTtl", "00:00:00")]
    [InlineData("NotFoundCacheTtl", "-01:00:00")]
    [InlineData("RequestTimeout", "00:00:00")]
    [InlineData("JobBudget", "00:00:00")]
    [InlineData("MaxAttempts", "0")]
    [InlineData("MaxRequestsPerSecond", "0")]
    [InlineData("MaxFollowUpGenerations", "0")]
    [InlineData("RecordingDirectory", " ")]
    public void Bad_SAT_configuration_fails_at_startup_with_clear_SAT_error(string key, string value)
    {
        var configuration = Config(new() { ["Sat:" + key] = value });
        var error = Assert.Throws<ArgumentException>(() => new ServiceCollection().AddSat(configuration));
        Assert.Contains("Sat:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_mode_requires_HTTPS_and_replay_cannot_record()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddSat(Config(new() { ["Sat:Mode"] = "real", ["Sat:Endpoint"] = "http://example.invalid/" })));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddSat(Config(new() { ["Sat:Mode"] = "replay", ["Sat:RecordResponses"] = "true" })));
        var services = new ServiceCollection().AddLogging(); services.AddSat(Config(new() { ["Sat:Mode"] = "real" }));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(SatContract.PublicEndpoint, provider.GetRequiredService<SatOptions>().EndpointUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("stub", false, "SoapSatClient")]
    [InlineData("real", false, "SoapSatClient")]
    [InlineData("stub", true, "RecordingSatClient")]
    [InlineData("real", true, "RecordingSatClient")]
    [InlineData("replay", false, "ReplaySatClient")]
    public void Configuration_alone_selects_the_adapter(string mode, bool record, string expected)
    {
        var services = new ServiceCollection().AddLogging(); services.AddSat(Config(new() { ["Sat:Mode"] = mode, ["Sat:RecordResponses"] = record.ToString() }));
        using var provider = services.BuildServiceProvider(); Assert.Equal(expected, provider.GetRequiredService<ISatClient>().GetType().Name);
    }

    [Fact]
    public async Task Real_recording_warns_at_startup_without_identifiers_or_network()
    {
        var capture = new LogCapture(); var services = new ServiceCollection().AddLogging(logging => logging.AddProvider(capture));
        services.AddSat(Config(new() { ["Sat:Mode"] = "real", ["Sat:RecordResponses"] = "true" }));
        await using var provider = services.BuildServiceProvider();
        foreach (var startup in provider.GetServices<IHostedService>()) await startup.StartAsync(TestContext.Current.CancellationToken);
        Assert.Contains(capture.Messages, message => message.Contains(".local", StringComparison.Ordinal) && message.Contains("recording", StringComparison.OrdinalIgnoreCase));
    }

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private sealed class LogCapture : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

[Collection("sat-api")]
public sealed class SatKeyRouteTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("replay", false, "succeeded")]
    [InlineData("stub", false, "succeeded")]
    [InlineData("stub", true, "failed")]
    public async Task Payload_key_validation_uses_the_same_mode_rules(string mode, bool record, string expected)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await using var app = fixture.App.WithWebHostBuilder(builder => { builder.UseSetting("Sat:Mode", mode); builder.UseSetting("Sat:RecordResponses", record.ToString()); });
        var id = await app.Services.GetRequiredService<CompanyScope>().ExecuteWriteAsync(seed.Company, (c, t, ct) => JobStore.EnqueueAsync(c, t,
            seed.Company, seed.Actor, "status_check", JsonSerializer.Serialize(new SatCheckPayload([seed.Invoice], seed.Uuid), JsonSerializerOptions.Web),
            null, 3, fixture.Clock.GetUtcNow(), ct), TestContext.Current.CancellationToken);
        var dispatcher = app.Services.GetRequiredService<JobDispatcher>();
        for (var i = 0; i < 100 && await fixture.Database.ScalarAsync<string>("SELECT state FROM job WHERE id=@id", new { id }) == "queued"; i++)
            await dispatcher.RunOneAsync("synthetic-mode-worker", TestContext.Current.CancellationToken);
        Assert.Equal(expected, await fixture.Database.ScalarAsync<string>("SELECT state FROM job WHERE id=@id", new { id }));
    }

    [Theory]
    [InlineData("stub", false, HttpStatusCode.Accepted)]
    [InlineData("real", false, HttpStatusCode.Accepted)]
    [InlineData("replay", false, HttpStatusCode.Accepted)]
    [InlineData("stub", true, HttpStatusCode.BadRequest)]
    public async Task Same_key_validation_is_mode_aware_and_recording_off_discards_it(string mode, bool record, HttpStatusCode expected)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        using var original = await fixture.LoginAsync(seed.Email, seed.Company);
        await using var app = fixture.App.WithWebHostBuilder(builder => { builder.UseSetting("Sat:Mode", mode); builder.UseSetting("Sat:RecordResponses", record.ToString()); });
        using var client = app.CreateClient(); client.DefaultRequestHeaders.Authorization = original.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Add("X-Company-Id", seed.Company.ToString());
        using var response = await client.PostAsJsonAsync($"/companies/{seed.Company}/cfdi/{seed.Uuid}/sat-check", new { anonymizedUuid = seed.Uuid }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Accepted)
        {
            var job = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("jobId").GetGuid();
            var payload = await fixture.Database.ScalarAsync<string>("SELECT payload::text FROM job WHERE id=@job", new { job });
            Assert.Equal(mode == "replay" ? seed.Uuid : null, JsonSerializer.Deserialize<SatCheckPayload>(payload!, JsonSerializerOptions.Web)!.AnonymizedUuid);
        }
    }

    [Theory]
    [InlineData("administrator", HttpStatusCode.Accepted)]
    [InlineData("accountant", HttpStatusCode.Forbidden)]
    public async Task Supplying_recording_key_is_administrator_only(string role, HttpStatusCode expected)
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        await owner.ExecuteAsync(new CommandDefinition("UPDATE membership SET role=@role::membership_role WHERE company_id=@company", new { role, company = seed.Company }, cancellationToken: TestContext.Current.CancellationToken));
        using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        using var response = await client.PostAsJsonAsync($"/companies/{seed.Company}/cfdi/{seed.Uuid}/sat-check", new { anonymizedUuid = Guid.Parse("00000000-0000-4000-8000-000000000751") }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Non_JSON_body_is_rejected_at_the_boundary()
    {
        var seed = await MatchingFiscalScenario.SeedAsync(fixture, 100, 100); using var client = await fixture.LoginAsync(seed.Email, seed.Company);
        using var body = new StringContent("not JSON", System.Text.Encoding.UTF8, "text/plain");
        using var response = await client.PostAsync($"/companies/{seed.Company}/cfdi/{seed.Uuid}/sat-check", body, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
