using System.Net;
using System.Net.Http.Json;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionConcurrencyTests(ApiFixture fixture)
{
    [Fact]
    public async Task Many_parallel_renewals_issue_exactly_one_pair_and_leave_the_family_active()
    {
        var user = await fixture.SeedUserAsync();
        using var host = new SessionTestHost(fixture);
        var original = await host.LoginAsync(user.Email);
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(index => host.RefreshAsync(original.RefreshToken)));
        try
        {
            var winner = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(15, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            var pair = (await winner.Content.ReadFromJsonAsync<SessionPair>(TestContext.Current.CancellationToken))!;
            Assert.Equal(original.SessionId, pair.SessionId);
            Assert.Equal(2, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token WHERE session_id=@sessionId", new { sessionId = pair.SessionId }));
            Assert.Equal(1, await fixture.Database.ScalarAsync<long>("SELECT count(*) FROM refresh_token WHERE session_id=@sessionId AND rotated_at IS NULL", new { sessionId = pair.SessionId }));
            Assert.False(await fixture.Database.ScalarAsync<bool>("SELECT revoked_at IS NOT NULL FROM auth_session WHERE id=@sessionId", new { sessionId = pair.SessionId }));
            using var active = await host.RefreshAsync(pair.RefreshToken);
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
