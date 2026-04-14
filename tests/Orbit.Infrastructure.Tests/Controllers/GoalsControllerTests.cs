using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public class GoalsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<GoalsController> _logger = Substitute.For<ILogger<GoalsController>>();
    private readonly GoalsController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public GoalsControllerTests()
    {
        _controller = new GoalsController(_mediator, _logger);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    // --- GetGoals ---

    [Fact]
    public async Task GetGoals_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetGoalsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(PaginatedResponse<GoalDto>)!));

        var result = await _controller.GetGoals();

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetGoals_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetGoalsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<PaginatedResponse<GoalDto>>("Pro required"));

        var result = await _controller.GetGoals();

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- GetGoalById ---

    [Fact]
    public async Task GetGoalById_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetGoalByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(GoalDetailDto)!));

        var result = await _controller.GetGoalById(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetGoalById_NotFound_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<GetGoalByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GoalDetailDto>("Goal not found"));

        var result = await _controller.GetGoalById(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetGoalById_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetGoalByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<GoalDetailDto>("Pro required"));

        var result = await _controller.GetGoalById(Guid.NewGuid(), CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- CreateGoal ---

    [Fact]
    public async Task CreateGoal_Success_ReturnsCreated()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var request = new GoalsController.CreateGoalRequest("Test Goal", null, 100m, "pages");
        var result = await _controller.CreateGoal(request, CancellationToken.None);

        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task CreateGoal_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<CreateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Validation failed"));

        var request = new GoalsController.CreateGoalRequest("Test Goal", null, 100m, "pages");
        var result = await _controller.CreateGoal(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateGoal_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<CreateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<Guid>("Pro required"));

        var request = new GoalsController.CreateGoalRequest("Test Goal", null, 100m, "pages");
        var result = await _controller.CreateGoal(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- UpdateGoal ---

    [Fact]
    public async Task UpdateGoal_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<UpdateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new GoalsController.UpdateGoalRequest("Updated", null, 200m, "pages");
        var result = await _controller.UpdateGoal(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task UpdateGoal_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<UpdateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new GoalsController.UpdateGoalRequest("Updated", null, 200m, "pages");
        var result = await _controller.UpdateGoal(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateGoal_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<UpdateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Pro required"));

        var request = new GoalsController.UpdateGoalRequest("Updated", null, 200m, "pages");
        var result = await _controller.UpdateGoal(Guid.NewGuid(), request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- UpdateProgress ---

    [Fact]
    public async Task UpdateProgress_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<UpdateGoalProgressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new GoalsController.UpdateProgressRequest(50m);
        var result = await _controller.UpdateProgress(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task UpdateProgress_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<UpdateGoalProgressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new GoalsController.UpdateProgressRequest(50m);
        var result = await _controller.UpdateProgress(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateProgress_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<UpdateGoalProgressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Pro required"));

        var request = new GoalsController.UpdateProgressRequest(50m);
        var result = await _controller.UpdateProgress(Guid.NewGuid(), request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- UpdateStatus ---

    [Fact]
    public async Task UpdateStatus_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<UpdateGoalStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new GoalsController.UpdateStatusRequest(GoalStatus.Active);
        var result = await _controller.UpdateStatus(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task UpdateStatus_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<UpdateGoalStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new GoalsController.UpdateStatusRequest(GoalStatus.Active);
        var result = await _controller.UpdateStatus(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateStatus_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<UpdateGoalStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Pro required"));

        var request = new GoalsController.UpdateStatusRequest(GoalStatus.Active);
        var result = await _controller.UpdateStatus(Guid.NewGuid(), request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- ReorderGoals ---

    [Fact]
    public async Task ReorderGoals_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<ReorderGoalsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new GoalsController.ReorderGoalsRequest([]);
        var result = await _controller.ReorderGoals(request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task ReorderGoals_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<ReorderGoalsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new GoalsController.ReorderGoalsRequest([]);
        var result = await _controller.ReorderGoals(request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ReorderGoals_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<ReorderGoalsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Pro required"));

        var request = new GoalsController.ReorderGoalsRequest([]);
        var result = await _controller.ReorderGoals(request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- LinkHabits ---

    [Fact]
    public async Task LinkHabits_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<LinkHabitsToGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var request = new GoalsController.LinkHabitsRequest([Guid.NewGuid()]);
        var result = await _controller.LinkHabits(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task LinkHabits_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<LinkHabitsToGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var request = new GoalsController.LinkHabitsRequest([Guid.NewGuid()]);
        var result = await _controller.LinkHabits(Guid.NewGuid(), request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task LinkHabits_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<LinkHabitsToGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Pro required"));

        var request = new GoalsController.LinkHabitsRequest([Guid.NewGuid()]);
        var result = await _controller.LinkHabits(Guid.NewGuid(), request, CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- GetGoalDetail ---

    [Fact]
    public async Task GetGoalDetail_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetGoalDetailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(GoalDetailWithMetricsResponse)!));

        var result = await _controller.GetGoalDetail(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetGoalDetail_NotFound_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<GetGoalDetailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GoalDetailWithMetricsResponse>("Goal not found"));

        var result = await _controller.GetGoalDetail(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetGoalDetail_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetGoalDetailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<GoalDetailWithMetricsResponse>("Pro required"));

        var result = await _controller.GetGoalDetail(Guid.NewGuid(), CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- GetGoalMetrics ---

    [Fact]
    public async Task GetGoalMetrics_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetGoalMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(GoalMetrics)!));

        var result = await _controller.GetGoalMetrics(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetGoalMetrics_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetGoalMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GoalMetrics>("Error"));

        var result = await _controller.GetGoalMetrics(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetGoalMetrics_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetGoalMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<GoalMetrics>("Pro required"));

        var result = await _controller.GetGoalMetrics(Guid.NewGuid(), CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- GetGoalReview ---

    [Fact]
    public async Task GetGoalReview_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetGoalReviewQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(GoalReviewResponse)!));

        var result = await _controller.GetGoalReview();

        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetGoalReview_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetGoalReviewQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<GoalReviewResponse>("Pro required"));

        var result = await _controller.GetGoalReview();

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    // --- DeleteGoal ---

    [Fact]
    public async Task DeleteGoal_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DeleteGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _controller.DeleteGoal(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteGoal_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DeleteGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Error"));

        var result = await _controller.DeleteGoal(Guid.NewGuid(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DeleteGoal_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<DeleteGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Pro required"));

        var result = await _controller.DeleteGoal(Guid.NewGuid(), CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }
}
