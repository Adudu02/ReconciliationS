extern alias api;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using ApiProgram = api::Program;

namespace Reconciliation.Tests;

internal sealed record SessionPair(string AccessToken, string? RefreshToken, int ExpiresIn)
{
    public JwtSecurityToken Jwt => new JwtSecurityTokenHandler().ReadJwtToken(AccessToken);
    public Guid SessionId => Guid.Parse(Jwt.Claims.Single(claim => claim.Type == "sid").Value);
}

internal sealed class SessionTestHost : IDisposable
{
    private readonly WebApplicationFactory<ApiProgram> app;
    public ManualTimeProvider Clock { get; } = new();
    public HttpClient Client { get; }

    public SessionTestHost(ApiFixture fixture, Action<IWebHostBuilder>? configure = null)
    {
        app = fixture.App.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
            configure?.Invoke(builder);
        });
        Client = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = new Uri("https://localhost") });
    }

    public async Task<SessionPair> LoginAsync(string email, string password = ApiFixture.Password)
    {
        using var response = await Client.PostAsJsonAsync("/auth/tokens", new { email, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
    }

    public Task<HttpResponseMessage> RefreshAsync(string? refreshToken) => Client.PostAsJsonAsync("/auth/refresh", new { refreshToken }, TestContext.Current.CancellationToken);
    public void Authorize(SessionPair pair, Guid? companyId = null)
    {
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pair.AccessToken);
        Client.DefaultRequestHeaders.Remove("X-Company-Id");
        if (companyId.HasValue) Client.DefaultRequestHeaders.Add("X-Company-Id", companyId.ToString());
    }

    public void AuthorizeAs(Guid userId, Guid? sessionId, int accessMinutes = 15)
    {
        var claims = new List<Claim> { new("sub", userId.ToString()) };
        if (sessionId.HasValue) claims.Add(new Claim("sid", sessionId.ToString()!));
        var jwt = new JwtSecurityToken("reconciliation", "reconciliation", claims,
            notBefore: Clock.GetUtcNow().UtcDateTime, expires: Clock.GetUtcNow().UtcDateTime.AddMinutes(accessMinutes),
            signingCredentials: new SigningCredentials(app.Services.GetRequiredService<SymmetricSecurityKey>(), SecurityAlgorithms.HmacSha256));
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
    }

    public void Dispose() { Client.Dispose(); app.Dispose(); }
}

[Collection("tenancy-api")]
public sealed class SessionLoginTests(ApiFixture fixture)
{
    [Fact]
    public async Task Successful_login_returns_256_bit_refresh_and_only_its_digest_is_stored()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var pair = await host.LoginAsync(user.Email);
        Assert.Equal(900, pair.ExpiresIn);
        Assert.NotNull(pair.RefreshToken);
        Assert.Equal(43, pair.RefreshToken.Length);
        Assert.Equal(32, WebEncoders.Base64UrlDecode(pair.RefreshToken).Length);
        Assert.Equal(TimeSpan.FromMinutes(15), pair.Jwt.ValidTo - pair.Jwt.ValidFrom);
        Assert.Equal(user.Id.ToString(), pair.Jwt.Subject);
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var stored = await owner.QuerySingleAsync<byte[]>(new CommandDefinition("SELECT token_hash FROM refresh_token WHERE session_id=@sessionId", new { sessionId = pair.SessionId }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(pair.RefreshToken)), stored), "Only the presented token's SHA-256 digest should be stored.");
        var storedRows = await owner.QuerySingleAsync<string>(new CommandDefinition("SELECT to_jsonb(t)::text FROM refresh_token t WHERE session_id=@sessionId", new { sessionId = pair.SessionId }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(storedRows.Contains(pair.RefreshToken, StringComparison.Ordinal), "A refresh credential must never be persisted in plaintext.");
        var second = await host.LoginAsync(user.Email);
        Assert.True(second.RefreshToken != pair.RefreshToken, "Independent logins must have distinct random credentials.");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    public async Task Access_token_expires_at_its_configured_lifetime(int minutes)
    {
        var (user, company) = await fixture.SeedCompanyWithAdminAsync();
        using var host = new SessionTestHost(fixture, builder => builder.UseSetting("Session:AccessTokenMinutes", minutes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var pair = await host.LoginAsync(user.Email);
        Assert.Equal(minutes * 60, pair.ExpiresIn);
        host.Authorize(pair, company);
        using var before = await host.Client.GetAsync($"/companies/{company}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        host.Clock.Advance(TimeSpan.FromMinutes(minutes));
        using var expired = await host.Client.GetAsync($"/companies/{company}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_login_creates_neither_session_nor_refresh_token(bool unknownEmail)
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var before = await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token");
        using var denied = await host.Client.PostAsJsonAsync("/auth/tokens", new { email = unknownEmail ? $"missing-{Guid.NewGuid():N}@example.invalid" : user.Email, password = "synthetic-wrong-password" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(0, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM auth_session WHERE user_id=@Id", new { user.Id }));
        Assert.Equal(before, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token"));
    }
}
