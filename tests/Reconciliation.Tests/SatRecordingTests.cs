using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reconciliation.Core.Sat;

namespace Reconciliation.Tests;

public sealed class SatRecordingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "synthetic-sat-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider clock = new();
    private static readonly string[] ResponseFields = ["cancellabilityRaw", "cancellationStatusRaw", "checkedAt", "codeRaw", "efosRaw", "stateRaw"];
    private static readonly Guid Key = Guid.Parse("00000000-0000-4000-8000-000000000751");
    private static SatStatusRequest Request(Guid? key = null) => new(SatContractTests.SyntheticUuid, "AAA010101AAA", "BBB010101BBB", 1939.27m, key);

    [Fact]
    public async Task Recording_writes_only_response_fields_and_check_time_then_replay_returns_them_without_HTTP()
    {
        var answer = SatContract.Parse(SatContractTests.Envelope(state: "Cancelado", cancellation: "Cancelado sin aceptación", efos: "100"));
        var recorder = Recorder(new FixedClient(answer), new TestLogger());
        Assert.Same(answer, await recorder.CheckAsync(Request(Key), TestContext.Current.CancellationToken));
        var files = Directory.GetFiles(directory); Assert.Single(files); Assert.Equal(Key.ToString("D") + ".json", Path.GetFileName(files[0]));
        var text = await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(text);
        Assert.Equal(ResponseFields, json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(clock.GetUtcNow(), json.RootElement.GetProperty("checkedAt").GetDateTimeOffset());
        foreach (var secret in new[] { Request().Uuid.ToString(), Request().IssuerRfc, Request().ReceiverRfc, "0000001939.270000", "Envelope", "rawText" }) Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        var replay = Replay();
        var returned = await replay.CheckAsync(Request(Key), TestContext.Current.CancellationToken);
        Assert.IsType<SatAnswered>(returned); Assert.Equal(answer.Reply with { RawText = "" }, returned.Reply);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("same")]
    public async Task Explicit_recorder_requires_a_different_nonempty_anonymised_key(string kind)
    {
        var recorder = Recorder(new FixedClient(), new TestLogger());
        var request = Request(kind == "missing" ? null : kind == "empty" ? Guid.Empty : Request().Uuid);
        await Assert.ThrowsAsync<ArgumentException>(() => Record(recorder, request, SatContract.Parse(SatContractTests.Envelope())));
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("uuid")]
    [InlineData("uuidCompact")]
    [InlineData("issuer")]
    [InlineData("receiver")]
    [InlineData("total")]
    public async Task Explicit_privacy_guard_rejects_echoed_identifiers_in_any_response_field(string kind)
    {
        var request = Request(Key);
        var secret = kind switch { "uuid" => request.Uuid.ToString("D"), "uuidCompact" => request.Uuid.ToString("N"), "issuer" => request.IssuerRfc, "receiver" => request.ReceiverRfc, _ => "0000001939.270000" };
        var response = SatContract.Parse(SatContractTests.Envelope(cancellation: "Future " + secret));
        await Assert.ThrowsAsync<ArgumentException>(() => Record(Recorder(new FixedClient(), new TestLogger()), request, response));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Automatic_missing_or_unsafe_key_skips_recording_and_warns_once_without_identifiers()
    {
        var logger = new TestLogger(); var client = new FixedClient(); var recorder = Recorder(client, logger);
        foreach (var key in new Guid?[] { null, Guid.Empty, Request().Uuid })
            Assert.Same(client.Response, await recorder.CheckAsync(Request(key), TestContext.Current.CancellationToken));
        client.Response = SatContract.Parse(SatContractTests.Envelope(efos: Request().ReceiverRfc));
        Assert.Same(client.Response, await recorder.CheckAsync(Request(Key), TestContext.Current.CancellationToken));
        Assert.Equal(4L, recorder.GetType().GetProperty("SkippedRecordings")!.GetValue(recorder));
        Assert.Single(logger.Messages); Assert.Equal("recording skipped: no anonymised key", logger.Messages[0]);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("fallback")]
    [InlineData("different")]
    public async Task Replay_accepts_equal_key_or_falls_back_to_request_UUID_and_never_resolves_HTTP(string mode)
    {
        await Record(Recorder(new FixedClient(), new TestLogger()), Request(Key), SatContract.Parse(SatContractTests.Envelope()));
        var request = mode == "different" ? Request(Key) : Request(mode == "equal" ? Key : null) with { Uuid = Key };
        await using var provider = Services("replay", false).BuildServiceProvider();
        var response = await provider.GetRequiredService<ISatClient>().CheckAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(SatState.Current, response.Reply.State.Value);
    }

    [Fact]
    public async Task Missing_or_malformed_recording_is_unavailable_with_no_network_fallback()
    {
        var replay = Replay();
        var missing = await Assert.ThrowsAsync<SatTransientException>(() => replay.CheckAsync(Request(Key), TestContext.Current.CancellationToken));
        Assert.Contains("no recording for key", missing.Message, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, Key.ToString("D") + ".json"), "{invalid", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<SatTransientException>(() => replay.CheckAsync(Request(Key), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Recording_off_ignores_supplied_key_and_writes_nothing()
    {
        await using var provider = Services("stub", false).BuildServiceProvider();
        var response = await provider.GetRequiredService<ISatClient>().CheckAsync(Request(Request().Uuid), TestContext.Current.CancellationToken);
        Assert.Equal(SatState.Current, response.Reply.State.Value); Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Committed_recordings_contain_only_allowlisted_synthetic_UUIDs_and_response_fields()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var files = Directory.GetFiles(Path.Combine(root, "samples", "sat-recordings"), "*.json"); Assert.NotEmpty(files);
        foreach (var file in files)
        {
            Assert.Equal(Key.ToString("D"), Path.GetFileNameWithoutExtension(file));
            var text = File.ReadAllText(file); using var document = JsonDocument.Parse(text);
            foreach (var property in document.RootElement.EnumerateObject()) Assert.Contains(property.Name, ResponseFields);
            Assert.DoesNotMatch(@"(?i)[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", text);
            Assert.DoesNotMatch(@"[A-ZÑ&]{3,4}[0-9]{6}[A-Z0-9]{3}", text);
        }
    }

    private ISatClient Recorder(ISatClient inner, ILogger logger) => (ISatClient)Activator.CreateInstance(Type("RecordingSatClient"), inner, directory, clock, logger)!;
    private ISatClient Replay() => (ISatClient)Activator.CreateInstance(Type("ReplaySatClient"), directory)!;
    private static Type Type(string name) { var type = typeof(ISatClient).Assembly.GetType("Reconciliation.Core.Sat." + name); Assert.NotNull(type); return type; }
    private static async Task Record(ISatClient recorder, SatStatusRequest request, SatStatusResponse response)
    {
        var method = recorder.GetType().GetMethod("RecordAsync"); Assert.NotNull(method);
        try { await (Task)method.Invoke(recorder, [request, response, TestContext.Current.CancellationToken])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private IServiceCollection Services(string mode, bool record)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Sat:Mode"] = mode, ["Sat:RecordResponses"] = record.ToString(), ["Sat:RecordingDirectory"] = directory }).Build();
        var services = new ServiceCollection().AddLogging(); services.AddSat(config);
        services.AddSingleton<IHttpClientFactory>(new TestHttpFactory(mode == "replay")); return services;
    }
    private sealed class TestHttpFactory(bool forbidden) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => forbidden ? throw new InvalidOperationException("Replay resolved HTTP") : new(new AnswerHandler());
    }
    private sealed class AnswerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SatContractTests.Envelope(), Encoding.UTF8, "text/xml") });
    }
    private sealed class FixedClient(SatStatusResponse? response = null) : ISatClient
    {
        public SatStatusResponse Response { get; set; } = response ?? SatContract.Parse(SatContractTests.Envelope());
        public Task<SatStatusResponse> CheckAsync(SatStatusRequest request, CancellationToken cancellationToken) => Task.FromResult(Response);
    }
    private sealed class TestLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
