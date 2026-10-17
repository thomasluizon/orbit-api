using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Marketing.Commands;
using Orbit.Application.FeatureFlags;
using Orbit.Application.FeatureFlags.Commands;
using Orbit.Application.FeatureFlags.Queries;
using Orbit.Application.Common;
using Microsoft.AspNetCore.Authorization;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AdminControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<AdminController> _logger = Substitute.For<ILogger<AdminController>>();
    private readonly AdminController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public AdminControllerTests()
    {
        _controller = new AdminController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Theory]
    [InlineData(FeatureFlagActions.Create)]
    [InlineData(FeatureFlagActions.EnableForAccounts)]
    [InlineData(FeatureFlagActions.DisableForAccounts)]
    [InlineData(FeatureFlagActions.EnableForEveryone)]
    [InlineData(FeatureFlagActions.DisableForEveryone)]
    public async Task ManageFeatureFlags_ForwardsActionAndIdentity(string action)
    {
        _mediator.Send(Arg.Any<ManageFeatureFlagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AdminFeatureFlag("flag", "Description", false, null, [])));

        var result = await _controller.ManageFeatureFlags(new(action, "flag", ["account@example.com"], "Description"), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        await _mediator.Received(1).Send(Arg.Is<ManageFeatureFlagsCommand>(command => command.AdminId == UserId && command.Action == action && command.Key == "flag"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListFeatureFlags_UsesSameQueryForGetAndPost()
    {
        _mediator.Send(Arg.Any<ListFeatureFlagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<AdminFeatureFlag>>([]));
        (await _controller.ListFeatureFlags(CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        (await _controller.ManageFeatureFlags(new("list"), CancellationToken.None)).Should().BeOfType<OkObjectResult>();
        await _mediator.Received(2).Send(Arg.Is<ListFeatureFlagsQuery>(query => query.AdminId == UserId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void FeatureFlags_RequireLiveAdminPolicyOnController()
    {
        typeof(AdminController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Should().Contain(attribute => attribute.Policy == AdminPolicy.Name);
    }

    [Theory]
    [InlineData(FeatureFlagActions.NotFound, 404)]
    [InlineData(FeatureFlagActions.AlreadyExists, 409)]
    [InlineData(FeatureFlagActions.UnknownAccounts, 400)]
    [InlineData(ErrorCodes.NoPermission, 403)]
    public async Task ManageFeatureFlags_MapsFailures(string code, int status)
    {
        _mediator.Send(Arg.Any<ManageFeatureFlagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AdminFeatureFlag>(error: "Failure", errorCode: code));
        var result = (ObjectResult)await _controller.ManageFeatureFlags(new("create", "flag"), CancellationToken.None);
        result.StatusCode.Should().Be(status);
    }

    private static AdminController.BroadcastRequest NewRequest() =>
        new("Subject", "Assunto", "<p>en</p>", "<p>pt</p>", null);

    [Fact]
    public async Task SendMarketingBroadcast_Success_ReturnsAccepted()
    {
        _mediator.Send(Arg.Any<SendMarketingBroadcastCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new MarketingBroadcastResult(5, false)));

        var result = await _controller.SendMarketingBroadcast(NewRequest(), CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
    }

    [Fact]
    public async Task SendMarketingBroadcast_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SendMarketingBroadcastCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<MarketingBroadcastResult>("Subject required"));

        var result = await _controller.SendMarketingBroadcast(NewRequest(), CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }
}
