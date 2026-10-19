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
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Middleware;

public class McpBoundaryRegressionTests
{
    [Theory]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"unknown/method\"}")]
    [InlineData("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"get_habits\"}}]")]
    [InlineData("{bad json")]
    public async Task UnrecognizedBody_IsRefusedBeforeDispatch(string body)
    {
        var (context, services) = CreateContext(body);
        using (services)
        {
            var forwarded = false;
            var app = new ApplicationBuilder(services);
            app.UseMcpSelectiveAuth();
            app.Run(_ => { forwarded = true; return Task.CompletedTask; });
            await app.Build()(context);
            forwarded.Should().BeFalse();
            context.Response.Body.Position = 0;
            using var response = await JsonDocument.ParseAsync(context.Response.Body);
            response.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32001);
        }
    }

    [Theory]
    [InlineData(true, false, "content_moderation_refused")]
    [InlineData(false, true, "content_moderation_unavailable")]
    public async Task ModerationFailure_RefusesWrappedTool(bool flagged, bool unavailable, string reason)
    {
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"execute_agent_operation_v2","arguments":{"operationId":"create_habit","arguments":{"title":"screen this"}}}}""";
        var (context, services) = CreateContext(body, new ModerationResult(flagged, unavailable, []));
        using (services)
        {
            var forwarded = false;
            var app = new ApplicationBuilder(services);
            app.UseMcpSelectiveAuth();
            app.Run(_ => { forwarded = true; return Task.CompletedTask; });
            await app.Build()(context);
            forwarded.Should().BeFalse();
            context.Response.Body.Position = 0;
            using var response = await JsonDocument.ParseAsync(context.Response.Body);
            response.RootElement.GetProperty("error").GetProperty("message").GetString().Should().Be(reason);
        }
    }

    private static (DefaultHttpContext, ServiceProvider) CreateContext(string body, ModerationResult? moderationResult = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim("api_key_id", "credential")], "Test"));
        var auth = Substitute.For<IAuthenticationService>();
        auth.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>())
            .Returns(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        var limiter = Substitute.For<IDistributedRateLimitService>();
        limiter.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 60, 1, DateTime.UtcNow.AddMinutes(1)));
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(moderationResult ?? new ModerationResult(false, false, []));
        var services = new ServiceCollection().AddLogging().AddSingleton(auth).AddSingleton(limiter).AddSingleton(moderation).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/mcp";
        context.Request.Method = "POST";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return (context, services);
    }
}
