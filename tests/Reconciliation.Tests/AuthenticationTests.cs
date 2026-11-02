using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class AuthenticationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Anonymous_request_reveals_nothing_about_company_existence()
    {
        using var client = fixture.App.CreateClient();
        foreach (var path in new[] { "/companies/00000000-0000-0000-0000-000000000001", "/companies/00000000-0000-0000-0000-000000000002" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Credentials_are_salted_slow_hashes_and_login_issues_a_token()
    {
        var user = await fixture.SeedUserAsync();
        var hasher = fixture.App.Services.GetRequiredService<IPasswordHasher<string>>();
        await using var owner = await fixture.Database.OpenOwnerAsync();
        var hash = await owner.QuerySingleAsync<string>(new CommandDefinition("SELECT password_hash FROM app_user WHERE id = @Id", new { user.Id }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotEqual(ApiFixture.Password, hash);
        Assert.NotEqual(hash, hasher.HashPassword(user.Email, ApiFixture.Password));
        var bytes = Convert.FromBase64String(hash);
        Assert.Equal(1, bytes[0]); // Identity v3: PBKDF2, iteration count, random salt, derived key.
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4)) >= 100_000);
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4)) >= 16);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user.Email, hash, ApiFixture.Password));
        using var client = fixture.App.CreateClient();
        using var login = await client.PostAsJsonAsync("/auth/tokens", new { email = user.Email, password = ApiFixture.Password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var pair = await login.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(900, pair.GetProperty("expiresIn").GetInt32());
        Assert.Equal(43, pair.GetProperty("refreshToken").GetString()!.Length);
        client.DefaultRequestHeaders.Authorization = new("Bearer", pair.GetProperty("accessToken").GetString());
        Assert.Equal(3, client.DefaultRequestHeaders.Authorization.Parameter!.Split('.').Length);
        using var response = await client.GetAsync("/companies/00000000-0000-0000-0000-000000000001", TestContext.Current.CancellationToken);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_password_unknown_user_and_tampered_token_are_refused()
    {
        var user = await fixture.SeedUserAsync();
        using var client = fixture.App.CreateClient();
        foreach (var email in new[] { user.Email, "missing@example.invalid" })
        {
            using var response = await client.PostAsJsonAsync("/auth/tokens", new { email, password = "incorrect-password" }, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        client.DefaultRequestHeaders.Authorization = new("Bearer", "tampered.token.value");
        using var denied = await client.GetAsync("/companies/00000000-0000-0000-0000-000000000001", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/openapi.json")]
    public async Task Health_and_OpenAPI_are_anonymous(string path)
    {
        using var client = fixture.App.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
