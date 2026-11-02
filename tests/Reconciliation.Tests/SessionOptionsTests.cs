using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Reconciliation.Tests;

[Collection("tenancy-api")]
public sealed class SessionOptionsTests(ApiFixture fixture)
{
    [Theory]
    [InlineData("AccessTokenMinutes", "4")]
    [InlineData("AccessTokenMinutes", "31")]
    [InlineData("IdleMinutes", "0")]
    [InlineData("IdleMinutes", "-1")]
    [InlineData("AbsoluteDays", "0")]
    [InlineData("AbsoluteDays", "2147483647")]
    [InlineData("ReuseGraceSeconds", "-1")]
    [InlineData("AllowedOrigins:0", "not-an-origin")]
    [InlineData("AllowedOrigins:0", "https://example.invalid/path")]
    public void Invalid_session_configuration_stops_startup_with_a_clear_message(string setting, string value)
    {
        using var app = fixture.App.WithWebHostBuilder(builder => builder.UseSetting("Session:" + setting, value));
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("Session", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Test_host_uses_a_controllable_time_provider()
    {
        var clock = Assert.IsType<ManualTimeProvider>(fixture.App.Services.GetRequiredService<TimeProvider>());
        var before = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(before.AddSeconds(1), clock.GetUtcNow());
    }
}
