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
        await stream.WaitAsync(TimeSpan.FromSeconds(2));

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

        (await stream.WaitAsync(TimeSpan.FromSeconds(4))).Should().BeOfType<EmptyResult>();
        Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray())
            .Should().Contain("event: ready").And.Contain("connectionId");
    }

    private static async Task WaitForReadyAsync(Stream body)
    {
        await WaitForTextAsync(body, "event: ready");
    }

    private static async Task WaitForTextAsync(Stream body, string text)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!Encoding.UTF8.GetString(((MemoryStream)body).ToArray()).Contains(text, StringComparison.Ordinal))
            await Task.Delay(10, timeout.Token);
    }
}
