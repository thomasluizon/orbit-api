using System.Reflection;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Common;
using Orbit.Application.Waitlist.Commands;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Tests.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Orbit.Api.Controllers;
using Orbit.Api.RateLimiting;

namespace Orbit.Infrastructure.Tests.Controllers;

public class WaitlistControllerRateLimitTests
{
    [Fact]
    public void Join_IsRateLimitedUnderWaitlistPolicy()
    {
        var attribute = GetAction(nameof(WaitlistController.Join))
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        attribute.Should().HaveCount(1);
    }

    [Fact]
    public void Join_UsesLeastPrivilegeLandingCorsPolicy()
    {
        var cors = GetAction(nameof(WaitlistController.Join))
            .GetCustomAttribute<EnableCorsAttribute>(inherit: false);

        cors.Should().NotBeNull();
        cors!.PolicyName.Should().Be("Landing");
    }

    [Fact]
    public void Controller_AllowsAnonymous()
    {
        typeof(WaitlistController)
            .GetCustomAttribute<AllowAnonymousAttribute>(inherit: false)
            .Should().NotBeNull();
    }

    [Fact]
    public void Confirm_IsNotRateLimited()
    {
        var attribute = GetAction(nameof(WaitlistController.Confirm))
            .GetCustomAttributes<DistributedRateLimitAttribute>(inherit: false)
            .ToList();

        attribute.Should().BeEmpty();
    }

    [Fact]
    public async Task Join_RotatingEmails_BoundsConfirmationSendsByIpCeiling()
    {
        using var scope = new RateLimitFilterTestScope();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var email = Substitute.For<IEmailService>();
        var tokens = Substitute.For<IWaitlistConfirmationTokenService>();
        tokens.CreateToken(Arg.Any<string>(), Arg.Any<string>()).Returns("confirmation-token");
        var options = Options.Create(new WaitlistSettings { ApiBaseUrl = "https://api.useorbit.org" });
        var handler = new JoinWaitlistCommandHandler(cache, tokens, email, options);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<JoinWaitlistCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<JoinWaitlistCommand>(), call.Arg<CancellationToken>()));
        var controller = new WaitlistController(mediator, options, NullLogger<WaitlistController>.Instance);
        var action = GetAction(nameof(WaitlistController.Join));

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var request = new WaitlistController.JoinWaitlistRequest($"a{attempt}@x.com");
            var context = scope.CreateContext(request, action);
            context.HttpContext.Request.Path = "/api/waitlist";
            controller.ControllerContext = new ControllerContext { HttpContext = context.HttpContext };
            await scope.ExecuteAsync(context, executeAction: async () =>
            {
                var result = await controller.Join(request, CancellationToken.None);
                result.Should().BeOfType<OkObjectResult>();
            });
            RateLimitFilterTestScope.AssertOutcome(context, attempt <= 15);
        }
        await email.Received(15).SendWaitlistConfirmationAsync(
            Arg.Any<string>(), Arg.Any<string>(), "en", Arg.Any<CancellationToken>());
    }

    private static MethodInfo GetAction(string actionName)
    {
        var method = typeof(WaitlistController).GetMethod(actionName, BindingFlags.Public | BindingFlags.Instance);
        method.Should().NotBeNull($"WaitlistController should expose a public action named '{actionName}'");
        return method!;
    }
}
