using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Api.Extensions;

namespace Orbit.Infrastructure.Tests.Middleware;

public class McpAuthorizationTests
{
    [Theory]
    [InlineData("initialize")]
    [InlineData("ping")]
    [InlineData("Initialize")]
    [InlineData("INITIALIZE")]
    [InlineData("Ping")]
    [InlineData("PING")]
    [InlineData("Notifications/initialized")]
    [InlineData("NOTIFICATIONS/cancelled")]
    [InlineData(" initialize")]
    [InlineData("initialize ")]
    [InlineData("ping\t")]
    [InlineData("notifications")]
    [InlineData("notifications/")]
    [InlineData("notifications/initialized")]
    [InlineData("notifications/cancelled")]
    [InlineData("notifications/progress")]
    [InlineData("notifications/message/nested")]
    [InlineData("tools/call")]
    [InlineData("tools/list")]
    [InlineData("resources/read")]
    [InlineData("resources/list")]
    [InlineData("prompts/get")]
    [InlineData("prompts/list")]
    [InlineData("completion/complete")]
    [InlineData("logging/setLevel")]
    public async Task Post_EveryMethodRequiresAuthentication(string method)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method });
        await AssertAuthenticationGate("POST", "/mcp", body, authenticated: false);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("DELETE", false)]
    [InlineData("GET", true)]
    [InlineData("DELETE", true)]
    public async Task SessionRequests_RequireAuthenticationBeforeDispatch(string method, bool authenticated)
    {
        await AssertAuthenticationGate(method, "/mcp", "", authenticated);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("ping")]
    [InlineData("notifications/initialized")]
    [InlineData("notifications/cancelled")]
    public async Task AuthenticatedLifecycleMethod_ReachesTransport(string method)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method });
        await AssertAuthenticationGate("POST", "/mcp", body, authenticated: true);
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"initialize\"}")]
    public async Task UnauthenticatedInvalidBody_IsChallengedBeforeParsing(string body)
    {
        await AssertAuthenticationGate("POST", "/mcp", body, authenticated: false);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/mcp-other")]
    public async Task UnrelatedPath_DoesNotRequireMcpAuthentication(string path)
    {
        await AssertAuthenticationGate("POST", path, "", authenticated: false, isMcp: false);
    }

    private static async Task AssertAuthenticationGate(
        string method, string path, string body, bool authenticated, bool isMcp = true)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test"));
        var auth = Substitute.For<IAuthenticationService>();
        auth.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(authenticated
                ? AuthenticateResult.Success(new AuthenticationTicket(principal, "Test"))
                : AuthenticateResult.NoResult());
        await using var services = new ServiceCollection().AddSingleton(auth).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("api.example.com");
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var forwarded = false;
        var pipeline = new ApplicationBuilder(services);
        pipeline.UseMcpSelectiveAuth();
        pipeline.Run(_ => { forwarded = true; return Task.CompletedTask; });

        await pipeline.Build()(context);

        forwarded.Should().Be(authenticated || !isMcp);
        if (isMcp && !authenticated)
        {
            context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
            context.Response.Headers.WWWAuthenticate.ToString().Should().Be(
                "Bearer resource_metadata=\"https://api.example.com/.well-known/oauth-protected-resource/mcp\"");
        }
        else
        {
            context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
            context.Response.Headers.Should().NotContainKey("WWW-Authenticate");
        }
        if (isMcp)
            await auth.Received(1).AuthenticateAsync(context, Arg.Any<string?>());
        else
            await auth.DidNotReceive().AuthenticateAsync(context, Arg.Any<string?>());
    }
}
