using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Extensions;
using Orbit.Api.Middleware;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Middleware;

public class McpMalformedRequestTests
{
    [Theory]
    [InlineData("initialize")]
    [InlineData("ping")]
    [InlineData("notifications/initialized")]
    [InlineData("notifications/cancelled")]
    public async Task Post_UnauthenticatedProtocolMethod_ReturnsOAuthChallenge(string method)
    {
        var logs = new RecordedLogs();
        await using var app = CreateApp(logs, authenticated: false);
        var context = await Post(app, """{"jsonrpc":"2.0","id":1,"method":"$method","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"orbit-auth-probe","version":"1"}}}""".Replace("$method", method, StringComparison.Ordinal));

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToString().Should().Be(
            "Bearer resource_metadata=\"https://api.useorbit.org/.well-known/oauth-protected-resource/mcp\"");
        context.Response.Headers.Should().NotContainKey("Mcp-Session-Id");
        context.Response.Body.Length.Should().Be(0);
    }

    [Theory]
    [InlineData("1.5", "initialize")]
    [InlineData("null", "initialize")]
    [InlineData("1.5", "ping")]
    [InlineData("null", "ping")]
    [InlineData("1.5", "tools/list")]
    [InlineData("null", "tools/list")]
    [InlineData("1.5", "tools/call")]
    [InlineData("null", "tools/call")]
    public async Task Post_InvalidId_ReturnsBadRequestWithoutErrorEvent(string id, string method)
    {
        var logs = new RecordedLogs();
        await using var app = CreateApp(logs);
        var context = await Post(app, """{"jsonrpc":"2.0","id":$id,"method":"$method","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"malformed-test","version":"1"},"name":"get_habits","arguments":{}}}""".Replace("$id", id, StringComparison.Ordinal).Replace("$method", method, StringComparison.Ordinal));

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().StartWith("application/json");
        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(context.Response.Body);
        response.RootElement.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        response.RootElement.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        response.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
        logs.Levels.Should().NotContain(level => level >= LogLevel.Error);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"request-one\"")]
    public async Task Post_ValidInitialize_PreservesRequestId(string id)
    {
        var logs = new RecordedLogs();
        await using var app = CreateApp(logs);
        var context = await Post(app, """{"jsonrpc":"2.0","id":$id,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"valid-test","version":"1"}}}""".Replace("$id", id, StringComparison.Ordinal));

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Headers["Mcp-Session-Id"].ToString().Should().NotBeNullOrEmpty();
        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        var data = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        using var response = JsonDocument.Parse(data);
        response.RootElement.GetProperty("id").GetRawText().Should().Be(id);
        response.RootElement.TryGetProperty("result", out _).Should().BeTrue();
        logs.Levels.Should().NotContain(level => level >= LogLevel.Error);
    }

    private static WebApplication CreateApp(RecordedLogs logs, bool authenticated = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders().AddProvider(logs);
        builder.Services.AddExceptionHandler<UnhandledExceptionHandler>();
        builder.Services.AddProblemDetails();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test"));
        var auth = Substitute.For<IAuthenticationService>();
        auth.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>())
            .Returns(authenticated
                ? AuthenticateResult.Success(new AuthenticationTicket(principal, "Test"))
                : AuthenticateResult.NoResult());
        var limiter = Substitute.For<IDistributedRateLimitService>();
        limiter.TryAcquireAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new DistributedRateLimitDecision(true, 60, 1, DateTime.UtcNow.AddMinutes(1)));
        builder.Services.AddSingleton(auth).AddSingleton(limiter);
        Orbit.Api.Extensions.ServiceCollectionExtensions.AddMcpToolServer(builder);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseRouting();
        app.UseMcpSelectiveAuth();
        app.UseEndpoints(endpoints => endpoints.MapMcp("/mcp"));
        return app;
    }

    private static async Task<DefaultHttpContext> Post(WebApplication app, string body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var scope = app.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = timeout.Token };
        context.Request.Method = "POST";
        context.Request.Path = "/mcp";
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("api.useorbit.org");
        context.Request.ContentType = "application/json";
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.Headers["MCP-Protocol-Version"] = "2025-11-25";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        await ((IApplicationBuilder)app).Build()(context);
        return context;
    }

    private sealed class RecordedLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<LogLevel> Levels { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Enqueue(logLevel);
        public void Dispose() { }
    }
}
