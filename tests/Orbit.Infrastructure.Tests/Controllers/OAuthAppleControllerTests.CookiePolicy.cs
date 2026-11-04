using FluentAssertions;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class OAuthAppleControllerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CookiePipeline_AppleFormPost_WithoutCookieRedirectsToBoundConsentGet(bool cancel)
    {
        await using var host = new OAuthPipelineHost(_controller, _flow);
        var cookie = await host.AuthorizeAsync();
        var start = await host.SendAsync("/oauth/apple/start?" + OAuthPipelineHost.Parameters, cookie);
        start.Response.StatusCode.Should().Be(302);
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(start.Response.Headers.Location.ToString()).Query)["state"]!;
        var callback = await host.SendAsync("/oauth/apple/callback", method: "POST", body: "code=code&state=" + state,
            origin: "https://appleid.apple.com");
        callback.Response.StatusCode.Should().Be(303);
        callback.Response.Headers.SetCookie.Should().BeEmpty();
        await OAuthControllerTests.AssertPipelineConsent(host, callback.Response.Headers.Location.ToString(), cookie, cancel);
    }
}
