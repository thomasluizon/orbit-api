using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class GoalToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly GoalTools _tools;
    private readonly ClaimsPrincipal _user;

    public GoalToolsTests()
    {
        _tools = new GoalTools(_mediator);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task ListGoals_Success_ReturnsFormattedList()
    {
        var goals = new List<GoalDto>
        {
            new(Guid.NewGuid(), "Run 100km", null, 100, 25, "km", GoalStatus.Active, GoalType.Standard,
                null, 0, DateTime.UtcNow, null, 25m, [], "On Track")
        };
        var paginated = new PaginatedResponse<GoalDto>(goals, 1, 50, 1, 1);
        _mediator.Send(Arg.Any<GetGoalsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(paginated));

        var result = await _tools.ListGoals(_user);

        result.Should().Contain("Run 100km");
        result.Should().Contain("25/100 km");
    }

    [Fact]
    public async Task ListGoals_Empty_ReturnsNoGoalsMessage()
    {
        var paginated = new PaginatedResponse<GoalDto>([], 1, 50, 0, 0);
        _mediator.Send(Arg.Any<GetGoalsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(paginated));

        var result = await _tools.ListGoals(_user);

        result.Should().Contain("No goals found");
    }

    [Fact]
    public async Task ListGoals_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetGoalsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PaginatedResponse<GoalDto>>("Something went wrong"));

        var result = await _tools.ListGoals(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetGoal_Success_ReturnsDetail()
    {
        var goalId = Guid.NewGuid();
        var detail = new GoalDetailDto(
            goalId, "Read 12 books", "One per month", 12, 3, "books",
            GoalStatus.Active, GoalType.Standard, new DateOnly(2026, 12, 31), 0,
            DateTime.UtcNow, null, 25m, [], []);

        _mediator.Send(Arg.Any<GetGoalByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(detail));

        var result = await _tools.GetGoal(_user, goalId.ToString());

        result.Should().Contain("Read 12 books");
        result.Should().Contain("3/12 books");
    }

    [Fact]
    public async Task GetGoal_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetGoalByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GoalDetailDto>("Goal not found"));

        var result = await _tools.GetGoal(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateGoal_Success_ReturnsCreatedMessage()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var result = await _tools.CreateGoal(_user, "New Goal", 100, "km");

        result.Should().Contain("Created goal 'New Goal'");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task CreateGoal_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<CreateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Pro required"));

        var result = await _tools.CreateGoal(_user, "Goal", 100, "km");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task UpdateGoal_Success_ReturnsUpdatedMessage()
    {
        var goalId = Guid.NewGuid();
        _mediator.Send(Arg.Any<UpdateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.UpdateGoal(_user, goalId.ToString(), "Updated", 200, "km");

        result.Should().Contain("Updated goal");
    }

    [Fact]
    public async Task UpdateGoal_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<UpdateGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Goal not found"));

        var result = await _tools.UpdateGoal(_user, Guid.NewGuid().ToString(), "Title", 100, "km");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DeleteGoal_Success_ReturnsDeletedMessage()
    {
        _mediator.Send(Arg.Any<DeleteGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.DeleteGoal(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Deleted goal");
    }

    [Fact]
    public async Task DeleteGoal_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<DeleteGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Goal not found"));

        var result = await _tools.DeleteGoal(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task LinkHabitsToGoal_Success_ReturnsLinkedMessage()
    {
        _mediator.Send(Arg.Any<LinkHabitsToGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var goalId = Guid.NewGuid();
        var habitId = Guid.NewGuid();
        var result = await _tools.LinkHabitsToGoal(_user, goalId.ToString(), habitId.ToString());

        result.Should().Contain("Linked 1 habits");
    }

    [Fact]
    public async Task LinkHabitsToGoal_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<LinkHabitsToGoalCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not found"));

        var result = await _tools.LinkHabitsToGoal(_user, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task UpdateGoalProgress_Success_ReturnsUpdatedMessage()
    {
        var goalId = Guid.NewGuid();
        _mediator.Send(Arg.Any<UpdateGoalProgressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.UpdateGoalProgress(_user, goalId.ToString(), 50);

        result.Should().Contain("Updated progress");
        result.Should().Contain("50");
    }

    [Fact]
    public async Task UpdateGoalProgress_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<UpdateGoalProgressCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not found"));

        var result = await _tools.UpdateGoalProgress(_user, Guid.NewGuid().ToString(), 50);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task UpdateGoalStatus_Success_ReturnsUpdatedMessage()
    {
        var goalId = Guid.NewGuid();
        _mediator.Send(Arg.Any<UpdateGoalStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.UpdateGoalStatus(_user, goalId.ToString(), "Completed");

        result.Should().Contain("Updated goal");
        result.Should().Contain("Completed");
    }

    [Fact]
    public async Task UpdateGoalStatus_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<UpdateGoalStatusCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not found"));

        var result = await _tools.UpdateGoalStatus(_user, Guid.NewGuid().ToString(), "Completed");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetGoalMetrics_Success_ReturnsFormattedMetrics()
    {
        var metrics = new GoalMetrics(75.5m, 1.2m, new DateOnly(2026, 6, 1), 60, "On Track", []);
        _mediator.Send(Arg.Any<GetGoalMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(metrics));

        var result = await _tools.GetGoalMetrics(_user, Guid.NewGuid().ToString());

        result.Should().Contain("75.5%");
        result.Should().Contain("1.20/day");
        result.Should().Contain("On Track");
    }

    [Fact]
    public async Task GetGoalMetrics_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetGoalMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GoalMetrics>("Goal not found"));

        var result = await _tools.GetGoalMetrics(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetGoalReview_Success_ReturnsReview()
    {
        var response = new GoalReviewResponse("You are on track!", false);
        _mediator.Send(Arg.Any<GetGoalReviewQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.GetGoalReview(_user);

        result.Should().Contain("You are on track!");
        result.Should().Contain("Goal Review");
    }

    [Fact]
    public async Task GetGoalReview_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetGoalReviewQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GoalReviewResponse>("Pro required"));

        var result = await _tools.GetGoalReview(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task ReorderGoals_Success_ReturnsReorderedMessage()
    {
        _mediator.Send(Arg.Any<ReorderGoalsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var json = $"[{{\"id\":\"{Guid.NewGuid()}\",\"position\":0}}]";
        var result = await _tools.ReorderGoals(_user, json);

        result.Should().Contain("Reordered 1 goals");
    }

    [Fact]
    public async Task ReorderGoals_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<ReorderGoalsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid"));

        var json = "[]";
        var result = await _tools.ReorderGoals(_user, json);

        result.Should().StartWith("Error: ");
    }
}
