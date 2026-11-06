using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using Reconciliation.Contracts;

namespace Reconciliation.Web;

public sealed class ApiProblemException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record BrowserFileUpload(string Name, byte[] Content);

public sealed class ApiClient(HttpClient http, SessionState session) : IDisposable
{
    private static readonly string[] ProblemFields = ["detail", "reason", "error", "title"];
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private static JsonTypeInfo<T> Info<T>() => (JsonTypeInfo<T>)WebJsonContext.Default.GetTypeInfo(typeof(T))!;

    public async Task LoginAsync(string email, string password, CancellationToken token = default)
    {
        using var request = AuthRequest("auth/tokens", JsonContent.Create(new LoginRequest(email, password), Info<LoginRequest>()));
        using var response = await http.SendAsync(request, token);
        await CheckAsync(response, token);
        var tokens = (await response.Content.ReadFromJsonAsync(Info<SessionTokens>(), token))!;
        session.Clear();
        session.Authenticate(tokens);
    }

    public async Task<bool> RestoreAsync(CancellationToken token = default)
    {
        await refreshGate.WaitAsync(token);
        try { return session.IsAuthenticated || await RefreshAsync(token); }
        finally { refreshGate.Release(); }
    }

    private async Task<bool> RefreshAsync(CancellationToken token)
    {
        using var request = AuthRequest("auth/refresh");
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) { session.Clear(); return false; }
        session.Authenticate((await response.Content.ReadFromJsonAsync(Info<SessionTokens>(), token))!);
        return true;
    }

    public async Task LogoutAsync(CancellationToken token = default)
    {
        using var response = await SendAsync(() => AuthRequest("auth/logout"), token);
        session.Clear();
    }

    private static HttpRequestMessage AuthRequest(string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Accept.ParseAdd("application/json, application/problem+json");
        request.Headers.Add("X-Refresh-Transport", "cookie");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken token)
    {
        var attemptedToken = session.AccessToken;
        using var request = create();
        Authorize(request);
        var response = await http.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            await refreshGate.WaitAsync(token);
            try
            {
                if (session.AccessToken == attemptedToken && !await RefreshAsync(token))
                    throw new ApiProblemException(401, "Your session has ended. Sign in again.");
            }
            finally { refreshGate.Release(); }
            using var retry = create();
            Authorize(retry);
            response = await http.SendAsync(retry, token);
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized) session.Clear();
        try { await CheckAsync(response, token); return response; }
        catch { response.Dispose(); throw; }
    }

    private void Authorize(HttpRequestMessage request)
    {
        if (request.Headers.Accept.Count == 0) request.Headers.Accept.ParseAdd("application/json, application/problem+json");
        request.Headers.Authorization = session.AccessToken is { } access ? new AuthenticationHeaderValue("Bearer", access) : null;
        if (session.Company is { } company && request.RequestUri!.OriginalString.StartsWith("companies/", StringComparison.Ordinal))
            request.Headers.Add("X-Company-Id", company.Id.ToString());
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has ended. Sign in again.",
            HttpStatusCode.Forbidden => "You do not have permission for this action. Check your company role and browser session settings.",
            HttpStatusCode.NotFound => "This item is unavailable in the selected company.",
            _ => "The request could not be completed. Try again."
        };
        if ((int)response.StatusCode < 500)
        {
            try
            {
                var problem = await response.Content.ReadFromJsonAsync(Info<JsonElement>(), token);
                foreach (var field in ProblemFields)
                    if (problem.TryGetProperty(field, out var text) && text.ValueKind == JsonValueKind.String) { message = text.GetString()!; break; }
                if (problem.TryGetProperty("state", out var state)) message = $"This suggestion was already decided ({state.GetString()}). Refresh the queue.";
            }
            catch (JsonException) { }
        }
        throw new ApiProblemException((int)response.StatusCode, message);
    }

    public async Task<DecisionResponse> DecideAsync(Guid linkId, DecisionRequest decision, CancellationToken token = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, CompanyPath($"links/{linkId}/decisions")) { Content = JsonContent.Create(decision, Info<DecisionRequest>()) }, token);
        return (await response.Content.ReadFromJsonAsync(Info<DecisionResponse>(), token))!;
    }

    public async Task<RunReceipt> StartRunAsync(CancellationToken token = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, CompanyPath("reconciliation-runs")), token);
        return (await response.Content.ReadFromJsonAsync(Info<RunReceipt>(), token))!;
    }

    public async Task ClassifyAsync(Guid movementId, ClassificationRequest classification, CancellationToken token = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, CompanyPath($"movements/{movementId}")) { Content = JsonContent.Create(classification, Info<ClassificationRequest>()) }, token);
    }

    public async Task<ImportReceipt> ImportAsync(string kind, IReadOnlyList<BrowserFileUpload> files, string bank, string accountReference, CancellationToken token = default)
    {
        using var response = await SendAsync(() =>
        {
            var content = new MultipartFormDataContent();
            foreach (var file in files) content.Add(new ByteArrayContent(file.Content), "files", file.Name);
            if (kind == "bank-statement") { content.Add(new StringContent(bank), "bank"); content.Add(new StringContent(accountReference), "accountReference"); }
            return new HttpRequestMessage(HttpMethod.Post, CompanyPath($"imports/{kind}")) { Content = content };
        }, token);
        return (await response.Content.ReadFromJsonAsync(Info<ImportReceipt>(), token))!;
    }

    public string CompanyPath(string resource) => $"companies/{session.Company?.Id ?? throw new ApiProblemException(400, "Select a company first.")}/{resource}";

    public async Task<T> GetAsync<T>(string path, CancellationToken token = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), token);
        return (await response.Content.ReadFromJsonAsync(Info<T>(), token))!;
    }

    public void Dispose() => refreshGate.Dispose();
}
