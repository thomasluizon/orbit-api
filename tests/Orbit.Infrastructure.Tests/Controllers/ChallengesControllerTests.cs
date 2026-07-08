using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Challenges.Commands;
using Orbit.Application.Challenges.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Infrastructure.Tests.Controllers;

public class ChallengesControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<ChallengesController> _logger = Substitute.For<ILogger<ChallengesController>>();
    private readonly ChallengesController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public ChallengesControllerTests()
    {
        _controller = new ChallengesController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    private static ChallengesController.CreateChallengeBody NewCreateBody() =>
        new(ChallengeType.CoopGoal, "Read daily", null, 30, new DateOnly(2026, 1, 1), null, null, null);

    [Fact]
    public async Task Create_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<CreateChallengeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));

        var result = await _controller.Create(NewCreateBody(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task Create_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<CreateChallengeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<Guid>("Pro required"));

        var result = await _controller.Create(NewCreateBody(), CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task Join_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<JoinChallengeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var body = new ChallengesController.JoinChallengeBody("CODE123", null);
        var result = await _controller.Join(body, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Join_AlreadyJoined_Returns409()
    {
        _mediator.Send(Arg.Any<JoinChallengeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Already joined", ErrorCodes.AlreadyJoinedChallenge));

        var body = new ChallengesController.JoinChallengeBody("CODE123", null);
        var result = await _controller.Join(body, CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(409);
    }

    [Fact]
    public async Task Leave_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<LeaveChallengeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.Leave(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Leave_NotFound_Returns404()
    {
        _mediator.Send(Arg.Any<LeaveChallengeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Challenge not found", ErrorCodes.ChallengeNotFound));

        var result = await _controller.Leave(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeAssignableTo<ObjectResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task SetHabits_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SetChallengeHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var body = new ChallengesController.SetChallengeHabitsBody([Guid.NewGuid()]);
        var result = await _controller.SetHabits(Guid.NewGuid(), body, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task GetMine_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetUserChallengesQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<ChallengeListItemResponse>>([]));

        var result = await _controller.GetMine(CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetDetail_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetChallengeDetailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<ChallengeDetailResponse>(default!));

        var result = await _controller.GetDetail(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }
}
