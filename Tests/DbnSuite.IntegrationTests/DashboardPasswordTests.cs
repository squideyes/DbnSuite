using System.Net;
using DbnSuite.IntegrationTests.Support;
using Shouldly;

namespace DbnSuite.IntegrationTests;

/// <summary>
/// The dashboard has no sign-in of its own: from the internet the only thing
/// in front of it is the password in Deploy/Caddyfile, so that file is what runs here.
/// </summary>
public sealed class DashboardPasswordTests(CaddyProxy caddy) : IClassFixture<CaddyProxy>
{
    private const string WrongPassword = "not-the-password";
    private const string WrongUser = "someone-else";

    [Fact]
    public async Task Nobody_reaches_the_dashboard_without_the_password()
    {
        var response = await caddy.GetDashboardAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldContain(h => h.Scheme == "Basic");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(CaddyProxy.BehindIt);
    }

    [Theory]
    [InlineData(CaddyProxy.User, WrongPassword)]
    [InlineData(WrongUser, CaddyProxy.Password)]
    public async Task A_wrong_user_or_password_is_refused(string user, string password)
    {
        var response = await caddy.GetDashboardAsync(user, password);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_right_password_reaches_the_worker()
    {
        var response = await caddy.GetDashboardAsync(CaddyProxy.User, CaddyProxy.Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(CaddyProxy.BehindIt);
    }
}
