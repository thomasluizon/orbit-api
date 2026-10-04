using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Domain.Events;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Events;

namespace Orbit.Infrastructure.Tests.Events;

public class EventsControllerTests
{
    private static readonly TimeSpan HangGuardTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(null, "123", 401)]
    [InlineData("invalid", "123", 401)]
    [InlineData("00000000-0000-0000-0000-000000000001", null, 401)]
    [InlineData("00000000-0000-0000-0000-000000000001", "invalid", 401)]
    public async Task Stream_RejectsMissingOrInvalidSessionAndExpiry(string? session, string? expiry, int status)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (session is not null) claims.Add(new Claim("orbit_session_id", session));
        if (expiry is not null) claims.Add(new Claim("exp", expiry));
        var controller = CreateController(new InMemoryAccountEventBus(), claims, CancellationToken.None);

        var result = await controller.Stream(CancellationToken.None);

        result.Should().BeOfType<UnauthorizedResult>().Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task Stream_RejectsExpiredTokenAndFullAccount()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var bus = new InMemoryAccountEventBus();
        var expired = CreateController(bus, StreamClaims(userId, sessionId, DateTimeOffset.UtcNow.AddSeconds(-5)), CancellationToken.None);
        (await expired.Stream(CancellationToken.None)).Should().BeOfType<UnauthorizedResult>();

        bus.TrySubscribe(userId, Guid.NewGuid(), null, out var first).Should().BeTrue();
        bus.TrySubscribe(userId, Guid.NewGuid(), null, out var second).Should().BeTrue();
        bus.TrySubscribe(userId, Guid.NewGuid(), null, out var third).Should().BeTrue();
        using (first!.Lease)
        using (second!.Lease)
        using (third!.Lease)
        {
            var controller = CreateController(bus, StreamClaims(userId, sessionId, DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);
            var result = await controller.Stream(CancellationToken.None);
            result.Should().BeOfType<StatusCodeResult>().Which.StatusCode.Should().Be(429);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stream_ReplaysKnownId_OrResyncsUnknownId(bool unknown)
    {
        var userId = Guid.NewGuid();
        var bus = new InMemoryAccountEventBus();
        bus.TrySubscribe(userId, Guid.NewGuid(), null, out var seed).Should().BeTrue();
        using (seed!.Lease)
        {
            bus.Publish(userId, new AccountEventPayload(1, [new AccountChange("habit", "create", [Guid.NewGuid()])]));
            seed.Reader.TryRead(out var first).Should().BeTrue();
            var replayId = Guid.NewGuid();
            bus.Publish(userId, new AccountEventPayload(1, [new AccountChange("notification", "update", [replayId])]));
            using var shutdown = new CancellationTokenSource();
            var controller = CreateController(bus, StreamClaims(userId, Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)), shutdown.Token);
            controller.Request.Headers["Last-Event-ID"] = unknown ? "unknown.1" : first!.Id;

            var stream = controller.Stream(CancellationToken.None);
            await WaitForTextAsync(controller.Response.Body, unknown ? "event: resync" : replayId.ToString());
            shutdown.Cancel();
            (await stream.WaitAsync(HangGuardTimeout)).Should().BeOfType<EmptyResult>();

            var body = Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray());
            if (unknown)
                body.Should().Contain("event: resync").And.NotContain(replayId.ToString());
            else
                body.Should().Contain($"id: {first!.Id.Split('.')[0]}.2").And.Contain(replayId.ToString());
        }
    }

    [Fact]
    public void CreateTicket_ReturnsTicketOnlyForCompleteClaims()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var controller = CreateController(new InMemoryAccountEventBus(), StreamClaims(userId, sessionId, DateTimeOffset.UtcNow.AddMinutes(5)), CancellationToken.None);
        controller.CreateTicket().Result.Should().BeOfType<OkObjectResult>();

        controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        ], "test"));
        controller.CreateTicket().Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Stream_SendsPublishedChangesOnce()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var bus = new InMemoryAccountEventBus();
        using var shutdown = new CancellationTokenSource();
        var host = Substitute.For<IHostApplicationLifetime>();
        host.ApplicationStopping.Returns(shutdown.Token);
        var settings = new JwtSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience"
        };
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("orbit_session_id", sessionId.ToString()),
            new Claim("exp", DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds().ToString())
        ], "test"));
        var controller = new EventsController(bus, new EventTicketService(Options.Create(settings)), host)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var stream = controller.Stream(CancellationToken.None);
        await WaitForReadyAsync(context.Response.Body);
        var habitId = Guid.NewGuid();
        bus.Publish(userId, new AccountEventPayload(1, [new AccountChange("habit", "update", [habitId])], "own-device"));
        await WaitForTextAsync(context.Response.Body, "event: changes");
        shutdown.Cancel();
        await stream.WaitAsync(HangGuardTimeout);

        var body = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
        body.Split("event: changes", StringSplitOptions.None).Should().HaveCount(2);
        body.Should().Contain(habitId.ToString()).And.Contain("own-device");
    }

    [Theory]
    [InlineData("shutdown")]
    [InlineData("revocation")]
    [InlineData("expiry")]
    public async Task Stream_ClosesForLifetimeBoundary(string boundary)
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var bus = new InMemoryAccountEventBus();
        using var shutdown = new CancellationTokenSource();
        var host = Substitute.For<IHostApplicationLifetime>();
        host.ApplicationStopping.Returns(shutdown.Token);
        var settings = new JwtSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience"
        };
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("orbit_session_id", sessionId.ToString()),
            new Claim("exp", DateTimeOffset.UtcNow.AddSeconds(boundary == "expiry" ? 2 : 60).ToUnixTimeSeconds().ToString())
        ], "test"));
        var controller = new EventsController(bus, new EventTicketService(Options.Create(settings)), host)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var stream = controller.Stream(CancellationToken.None);
        await WaitForReadyAsync(context.Response.Body);
        switch (boundary)
        {
            case "shutdown":
                shutdown.Cancel();
                break;
            case "revocation":
                bus.CloseSession(sessionId);
                break;
        }

        (await stream.WaitAsync(HangGuardTimeout)).Should().BeOfType<EmptyResult>();
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray())
            .Should().Contain("event: ready").And.Contain("connectionId");
    }

    private static async Task WaitForReadyAsync(Stream body)
    {
        await WaitForTextAsync(body, "event: ready");
    }

    private static List<Claim> StreamClaims(Guid userId, Guid sessionId, DateTimeOffset expiry) =>
    [
        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
        new Claim("orbit_session_id", sessionId.ToString()),
        new Claim("exp", expiry.ToUnixTimeSeconds().ToString())
    ];

    private static EventsController CreateController(
        IAccountEventBus bus, IEnumerable<Claim> claims, CancellationToken stopping)
    {
        var host = Substitute.For<IHostApplicationLifetime>();
        host.ApplicationStopping.Returns(stopping);
        var settings = new JwtSettings
        {
            SecretKey = "test-secret-key-that-is-at-least-32-bytes-long-for-hmac",
            Issuer = "test-issuer",
            Audience = "test-audience"
        };
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) };
        context.Response.Body = new MemoryStream();
        return new EventsController(bus, new EventTicketService(Options.Create(settings)), host)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static async Task WaitForTextAsync(Stream body, string text)
    {
        using var timeout = new CancellationTokenSource(HangGuardTimeout);
        while (!Encoding.UTF8.GetString(((MemoryStream)body).ToArray()).Contains(text, StringComparison.Ordinal))
            await Task.Delay(10, timeout.Token);
    }
}
