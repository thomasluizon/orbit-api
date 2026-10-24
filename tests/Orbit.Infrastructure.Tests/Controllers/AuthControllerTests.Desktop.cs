using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Middleware;
using Orbit.Api.RateLimiting;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class AuthControllerTests
{
    [Fact]
    public void DesktopHandoff_RequiresAuthentication_ExchangeIsAnonymous_BothUseAuthLimiter()
    {
        var create = typeof(AuthController).GetMethod(nameof(AuthController.CreateDesktopHandoff))!;
        var exchange = typeof(AuthController).GetMethod(nameof(AuthController.ExchangeDesktopHandoff))!;

        create.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();
        create.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        exchange.GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull();
        foreach (var method in new[] { create, exchange })
            method.CustomAttributes.Single(attribute => attribute.AttributeType == typeof(DistributedRateLimitAttribute))
                .ConstructorArguments[0].Value.Should().Be("auth");
    }

    [Fact]
    public async Task DesktopHandoff_UsesAuthenticatedAccountAndReturnsOnlyCodeAndLifetime()
    {
        var response = new DesktopHandoffResponse("opaque-code", 60);
        _mediator.Send(Arg.Any<CreateDesktopHandoffCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(response));

        var result = await _controller.CreateDesktopHandoff(new("state", "challenge", "S256", "redirect"), default);

        ((OkObjectResult)result).Value.Should().Be(response);
        await _mediator.Received(1).Send(new CreateDesktopHandoffCommand(UserId, "state", "challenge", "S256", "redirect"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DesktopExchange_ReturnsExistingLoginPayload()
    {
        var response = new LoginResponse(UserId, "access", "Name", "desktop@example.com", RefreshToken: "refresh");
        _mediator.Send(Arg.Any<ExchangeDesktopHandoffCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(response));

        var result = await _controller.ExchangeDesktopHandoff(new("code", "state", "verifier", "redirect"), default);

        ((OkObjectResult)result).Value.Should().Be(response);
        await _mediator.Received(1).Send(new ExchangeDesktopHandoffCommand("code", "state", "verifier", "redirect"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DesktopExchange_RejectedBinding_ReturnsGenericUnauthorized()
    {
        _mediator.Send(Arg.Any<ExchangeDesktopHandoffCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LoginResponse>(DesktopHandoff.InvalidHandoff));

        var result = (ObjectResult)await _controller.ExchangeDesktopHandoff(new("code", "state", "verifier", "redirect"), default);

        result.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        result.Value.Should().BeOfType<ErrorResponse>().Which.Error.Should().Be(DesktopHandoff.InvalidHandoff);
    }
}
