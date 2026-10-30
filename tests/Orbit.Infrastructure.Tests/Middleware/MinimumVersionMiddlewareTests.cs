using System.Text.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Middleware;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Tests.Middleware;

/// <summary>
/// The minimum-supported-version gate. Returns 426 only when the client's
/// X-App-Version is provably below the configured floor; a missing or
/// unparseable header always falls through (fail-safe allow) so clients that
/// predate the header are never stranded.
/// </summary>
public class MinimumVersionMiddlewareTests
{
    private const string FloorVersion = "1.1.4";

    private static IAppConfigService BuildConfigService(string floor)
    {
        var configService = Substitute.For<IAppConfigService>();
        configService
            .GetAsync(AppConfigKeys.MinSupportedVersion, "0.0.0", Arg.Any<CancellationToken>())
            .Returns(floor);
        return configService;
    }

    private static DefaultHttpContext BuildContext(string? clientVersion)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (clientVersion is not null)
            context.Request.Headers["X-App-Version"] = clientVersion;
        return context;
    }

    private static async Task<bool> InvokeMiddleware(DefaultHttpContext context, IAppConfigService configService,
        ILogger<MinimumVersionMiddleware>? logger = null)
    {
        var nextCalled = false;
        var middleware = new MinimumVersionMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            logger ?? NullLogger<MinimumVersionMiddleware>.Instance);

        await middleware.InvokeAsync(context, configService);
        return nextCalled;
    }

    private static async Task<(bool NextCalled, DefaultHttpContext Context)> InvokeWithVersion(
        string? clientVersion,
        string floor = FloorVersion)
    {
        var context = BuildContext(clientVersion);
        var nextCalled = await InvokeMiddleware(context, BuildConfigService(floor));
        return (nextCalled, context);
    }

    [Fact]
    public async Task InvokeAsync_VersionBelowFloor_Returns426WithUpgradeBody()
    {
        var (nextCalled, context) = await InvokeWithVersion("1.1.3");

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        var root = document.RootElement;
        root.GetProperty("errorCode").GetString().Should().Be(ErrorCodes.UpgradeRequired);
        root.GetProperty("upgradeRequired").GetBoolean().Should().BeTrue();
        root.GetProperty("minVersion").GetString().Should().Be(FloorVersion);
        root.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InvokeAsync_VersionEqualToFloor_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion("1.1.4");

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task InvokeAsync_VersionAboveFloor_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion("1.2.0");

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_MissingHeader_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion(clientVersion: null);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_BlankHeader_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion("   ");

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_UnparseableHeader_CallsNext()
    {
        var (nextCalled, context) = await InvokeWithVersion("banana");

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_IgnoresPrereleaseSuffixWhenComparing()
    {
        var (nextCalled, _) = await InvokeWithVersion("1.1.4-rc1");

        nextCalled.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("vNext")]
    public async Task InvokeAsync_NonComparableHeader_SkipsConfigReadBeforeAuth(string? clientVersion)
    {
        var configService = BuildConfigService(FloorVersion);
        var context = BuildContext(clientVersion);

        var nextCalled = await InvokeMiddleware(context, configService);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        await configService.DidNotReceive().GetAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("0.9.0")]
    [InlineData("1.2.0")]
    [InlineData("2.0.0-rc1")]
    public async Task InvokeAsync_ComparableHeader_ReadsConfigExactlyOnce(string clientVersion)
    {
        var configService = BuildConfigService(FloorVersion);
        var context = BuildContext(clientVersion);

        await InvokeMiddleware(context, configService);

        await configService.Received(1).GetAsync(
            AppConfigKeys.MinSupportedVersion, "0.0.0", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, 40)]
    [InlineData(false, 240)]
    [InlineData(true, 40)]
    public async Task InvokeAsync_RejectedVersion_LogsCallerWithoutCredentials(bool routed, int userAgentLength)
    {
        var context = BuildContext("1.0.0");
        context.Request.Method = "GET";
        context.Request.Path = routed ? "/api/habits/private-habit-id" : "/api/habits";
        context.Request.QueryString = new QueryString("?date=x&email=private@example.org");
        context.Request.Headers.Authorization = "Bearer private-token";
        context.Request.Headers.UserAgent = new string('a', userAgentLength);
        context.Request.Body = new MemoryStream("private-body"u8.ToArray());
        context.TraceIdentifier = "request-1902";
        if (routed)
            context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
                RoutePatternFactory.Parse("/api/habits/{id}"), 0, EndpointMetadataCollection.Empty, "habit"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, "private@example.org")], "JwtBearer"));
        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(context, null)
            .Returns(AuthenticateResult.Success(new AuthenticationTicket(principal, "JwtBearer")));
        using var services = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider();
        context.RequestServices = services;
        context.User.Identity!.IsAuthenticated.Should().BeFalse();
        var logger = new CapturingLogger();

        var nextCalled = await InvokeMiddleware(context, BuildConfigService("1.3.91"), logger);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Fields.Should().Contain("ClientVersion", "1.0.0").And.Contain("MinVersion", "1.3.91")
            .And.Contain("RequestId", "request-1902")
            .And.Contain("Request", routed ? "GET /api/habits/{id}" : "GET /api/habits")
            .And.Contain("UserAgent", new string('a', Math.Min(userAgentLength, 200)))
            .And.Contain("Authenticated", true);
        var logged = entry.Message + JsonSerializer.Serialize(entry.Fields);
        foreach (var secret in new[] { "private-token", "private@example.org", "date=x", "private-body", "private-habit-id" })
            logged.Should().NotContain(secret);
        entry.Level.Should().Be(LogLevel.Information);
        entry.EventId.Id.Should().Be(1);
        await authentication.Received(1).AuthenticateAsync(context, null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_RejectedVersion_LogsUnauthenticatedForMissingOrInvalidCredentials(bool invalid)
    {
        var context = BuildContext("1.0.0");
        if (invalid)
            context.Request.Headers.Authorization = "Bearer invalid-token";
        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(context, null).Returns(invalid
            ? AuthenticateResult.Fail("Invalid token")
            : AuthenticateResult.NoResult());
        using var services = new ServiceCollection().AddSingleton(authentication).BuildServiceProvider();
        context.RequestServices = services;
        var logger = new CapturingLogger();

        await InvokeMiddleware(context, BuildConfigService("1.3.91"), logger);

        context.Response.StatusCode.Should().Be(StatusCodes.Status426UpgradeRequired);
        logger.Entries.Should().ContainSingle().Subject.Fields.Should().Contain("Authenticated", false);
        logger.Entries[0].Message.Should().NotContain("invalid-token");
        await authentication.Received(1).AuthenticateAsync(context, null);
    }

    [Fact]
    public async Task InvokeAsync_MissingHeader_DoesNotLogOrAuthenticate()
    {
        var context = BuildContext(null);
        var logger = new CapturingLogger();

        var nextCalled = await InvokeMiddleware(context, BuildConfigService("1.3.91"), logger);

        nextCalled.Should().BeTrue();
        logger.Entries.Should().BeEmpty();
    }

    private sealed class CapturingLogger : ILogger<MinimumVersionMiddleware>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary();
            Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), fields));
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message, Dictionary<string, object?> Fields);
}
