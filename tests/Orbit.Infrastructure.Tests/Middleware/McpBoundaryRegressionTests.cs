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
using ModelContextProtocol.Server;
using Orbit.Api.Idempotency;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat.Commands;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;
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

    [Theory]
    [InlineData("session-one", "session-two", null, 2)]
    [InlineData("session-one", "session-two", "explicit-retry", 2)]
    [InlineData(null, null, null, 3)]
    [InlineData(null, null, "explicit-retry", 1)]
    public async Task MutationReplay_UsesValidatedSessionOrExplicitKey(string? firstSession, string? nextSession, string? explicitKey, int expectedApplications)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var db = database.Context;
        var user = Orbit.Domain.Entities.User.Create("Replay", "replay@example.com").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var applications = 0;
        var responses = new List<string>();
        async Task Run(string? sessionId)
        {
            var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"execute_agent_operation_v2","arguments":{"operationId":"create_tag","arguments":{"name":"Tag"}}}}""";
            var (context, services) = CreateContext(body);
            using (services)
            {
                context.Request.Headers["Mcp-Session-Id"] = sessionId;
                if (explicitKey is not null)
                    context.Request.Headers["Idempotency-Key"] = explicitKey;
                var app = new ApplicationBuilder(services);
                app.UseMcpSelectiveAuth();
                Task<AgentExecuteOperationResponse> Apply(CancellationToken token)
                {
                    applications++;
                    return Task.FromResult(new AgentExecuteOperationResponse(new("create_tag", "create_tag", AgentRiskClass.Low,
                        AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: new { application = applications })));
                }
                RequestDelegate dispatch = async requestContext =>
                {
                    requestContext.User = new ClaimsPrincipal(new ClaimsIdentity([
                        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim("api_key_id", "credential")], "Test"));
                    var server = Substitute.For<McpServer>();
                    server.SessionId.Returns(sessionId);
                    requestContext.Features.Set(server);
                    var request = new ExecuteAgentMutationCommand(new(user.Id, "create_tag",
                        JsonSerializer.SerializeToElement(new { name = "Tag" }), AgentExecutionSurface.Mcp, AgentAuthMethod.ApiKey));
                    var behavior = new IdempotencyBehavior<ExecuteAgentMutationCommand, AgentExecuteOperationResponse>(
                        new HttpIdempotencyContext(new HttpContextAccessor { HttpContext = requestContext }),
                        new IdempotencyStore(db), new UnitOfWork(db, new DatabaseConnectionSettings()));
                    var response = await behavior.Handle(request, Apply, default);
                    responses.Add(JsonSerializer.Serialize(response));
                };
                app.Run(dispatch);
                await app.Build()(context);
            }
        }
        await Run(firstSession);
        await Run(firstSession);
        await Run(nextSession);
        applications.Should().Be(expectedApplications);
        if (firstSession is not null || explicitKey is not null)
            responses[1].Should().Be(responses[0]);
        if (nextSession is not null)
            responses[2].Should().NotBe(responses[0]);
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
