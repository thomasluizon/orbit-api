using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Accountability.Commands;
using Orbit.Application.Accountability.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Controllers;

public class AccountabilityControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<AccountabilityController> _logger = Substitute.For<ILogger<AccountabilityController>>();
    private readonly AccountabilityController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public AccountabilityControllerTests()
    {
        _controller = new AccountabilityController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task GetPairs_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetAccountabilityPairsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<AccountabilityPairsResponse>(default!));

        var result = await _controller.GetPairs(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetPairs_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetAccountabilityPairsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<AccountabilityPairsResponse>("Pro required"));

        var result = await _controller.GetPairs(CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Invite_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<InviteAccountabilityBuddyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));

        var body = new AccountabilityController.InviteAccountabilityBuddyBody(
            Guid.NewGuid(), AccountabilityCadence.Daily, [Guid.NewGuid()]);
        var result = await _controller.Invite(body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Invite_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<InviteAccountabilityBuddyCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Buddy not found"));

        var body = new AccountabilityController.InviteAccountabilityBuddyBody(
            Guid.NewGuid(), AccountabilityCadence.Weekly, []);
        var result = await _controller.Invite(body, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task Accept_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<AcceptAccountabilityPairCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var body = new AccountabilityController.AcceptAccountabilityPairBody([Guid.NewGuid()]);
        var result = await _controller.Accept(Guid.NewGuid(), body, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task End_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<EndAccountabilityPairCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.End(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SetHabits_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetAccountabilityHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var body = new AccountabilityController.SetAccountabilityHabitsBody([Guid.NewGuid()]);
        var result = await _controller.SetHabits(Guid.NewGuid(), body, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetCheckIns_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetAccountabilityCheckInsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<AccountabilityCheckInsPage>(default!));

        var result = await _controller.GetCheckIns(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CheckIn_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<CheckInAccountabilityCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));

        var body = new AccountabilityController.CheckInAccountabilityBody("On track");
        var result = await _controller.CheckIn(Guid.NewGuid(), body, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CheckIn_AlreadyCheckedIn_Returns409()
    {
        _mediator.Send(Arg.Any<CheckInAccountabilityCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Already checked in", ErrorCodes.AlreadyCheckedIn));

        var body = new AccountabilityController.CheckInAccountabilityBody(null);
        var result = await _controller.CheckIn(Guid.NewGuid(), body, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(409);
    }
}
