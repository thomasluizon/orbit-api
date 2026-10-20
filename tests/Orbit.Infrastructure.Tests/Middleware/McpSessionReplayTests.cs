using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.Idempotency;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Middleware;

public class McpSessionReplayTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("explicit-retry")]
    public async Task MapMcp_TwoValidatedSessions_ReplaysWithinSessionAndSeparatesSessions(string? explicitKey)
    {
        using var database = new SqliteOrbitDbContextFactory();
        var user = User.Create("Replay", "replay@example.com").Value;
        database.Context.Users.Add(user);
        await database.Context.SaveChangesAsync();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim("api_key_id", "credential")], "Test"));
        var auth = Substitute.For<IAuthenticationService>();
        auth.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>())
            .Returns(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ModerationResult(false, false, []));
        var limiter = Substitute.For<IDistributedRateLimitService>();
        limiter.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 60, 1, DateTime.UtcNow.AddMinutes(1)));
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(auth).AddSingleton(moderation).AddSingleton(limiter).AddHttpContextAccessor();
        var applications = 0;
        builder.Services.AddMcpServer().WithHttpTransport().WithCallToolHandler(async (_, token) =>
        {
            var context = new HttpContextAccessor();
            var work = new UnitOfWork(database.Context, new DatabaseConnectionSettings());
            var behavior = new IdempotencyBehavior<ExecuteAgentMutationCommand, AgentExecuteOperationResponse>(
                new HttpIdempotencyContext(context), new IdempotencyStore(database.Context), work);
            var request = new ExecuteAgentMutationCommand(new(user.Id, "create_tag",
                JsonSerializer.SerializeToElement(new { name = "Tag" }), AgentExecutionSurface.Mcp, AgentAuthMethod.ApiKey));
            var result = await behavior.Handle(request, _ =>
            {
                applications++;
                return Task.FromResult(new AgentExecuteOperationResponse(new("create_tag", "create_tag", AgentRiskClass.Low,
                    AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: new { application = applications })));
            }, token);
            return new CallToolResult { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result) }] };
        });
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMcpSelectiveAuth();
        app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
        var pipeline = ((IApplicationBuilder)app).Build();
        var accessor = app.Services.GetRequiredService<IHttpContextAccessor>();

        async Task<DefaultHttpContext> Post(string body, string? session = null, bool authenticated = true)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var scope = app.Services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = timeout.Token };
            context.User = authenticated ? principal : new ClaimsPrincipal();
            context.Request.Method = "POST";
            context.Request.Path = "/mcp";
            context.Request.ContentType = "application/json";
            context.Request.Headers.Accept = "application/json, text/event-stream";
            context.Request.Headers["MCP-Protocol-Version"] = "2025-11-25";
            if (session is not null)
                context.Request.Headers["Mcp-Session-Id"] = session;
            if (explicitKey is not null)
                context.Request.Headers["Idempotency-Key"] = explicitKey;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Response.Body = new MemoryStream();
            accessor.HttpContext = context;
            try { await pipeline(context); }
            finally { accessor.HttpContext = null; }
            return context;
        }

        const string initialize = """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"replay-test","version":"1"}}}""";
        const string mutation = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"execute_agent_operation_v2","arguments":{"operationId":"create_tag","arguments":{"name":"Tag"}}}}""";
        var first = await Post(initialize);
        first.Response.StatusCode.Should().Be(200);
        var firstSession = first.Response.Headers["Mcp-Session-Id"].ToString();
        firstSession.Should().NotBeNullOrEmpty();
        var second = await Post(initialize);
        var secondSession = second.Response.Headers["Mcp-Session-Id"].ToString();
        secondSession.Should().NotBeNullOrEmpty().And.NotBe(firstSession);
        await Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", firstSession);
        await Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", secondSession);
        var response = await Post(mutation, firstSession);
        var replay = await Post(mutation, firstSession);
        response.Response.StatusCode.Should().Be(200);
        static string Payload(DefaultHttpContext context)
        {
            var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
            var data = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
            using var json = JsonDocument.Parse(data);
            return json.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        }
        Payload(replay).Should().Be(Payload(response));
        applications.Should().Be(1);
        var other = await Post(mutation, secondSession);
        Payload(other).Should().NotBe(Payload(response));
        applications.Should().Be(2);
        database.Context.ProcessedRequests.Count().Should().Be(2);
        var invalid = await Post(mutation, "invented-session");
        invalid.Response.StatusCode.Should().Be(404);
        applications.Should().Be(2);
    }
}
