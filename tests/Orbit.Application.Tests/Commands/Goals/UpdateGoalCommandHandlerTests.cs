using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Common;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Goals;

public class UpdateGoalCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<GoalProgressLog> _progressLogRepo = Substitute.For<IGenericRepository<GoalProgressLog>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly UpdateGoalCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid GoalId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 6, 13);

    public UpdateGoalCommandHandlerTests()
    {
        _handler = new UpdateGoalCommandHandler(
            _goalRepo, _progressLogRepo, _payGate, _userDateService, _gamificationService, _unitOfWork,
            Substitute.For<ILogger<UpdateGoalCommandHandler>>());
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
    }

    private void SetupGoalFound(Goal goal)
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);
    }

    [Fact]
    public async Task Handle_ValidUpdate_UpdatesGoalAndSaves()
    {
        var goal = Goal.Create(UserId, "Old title", 100, "km").Value;
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "New title", "Updated desc", 200, "miles", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Title.Should().Be("New title");
        goal.Description.Should().Be("Updated desc");
        goal.TargetValue.Should().Be(200);
        goal.Unit.Should().Be("miles");
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GoalNotFound_ReturnsFailure()
    {
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Goal?)null);

        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.GoalNotFound.Message);
        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyTitle_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Old title", 100, "km").Value;
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title");
    }

    [Fact]
    public async Task Handle_ZeroTargetValue_ReturnsFailure()
    {
        var goal = Goal.Create(UserId, "Title", 100, "km").Value;
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 0, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value");
    }

    [Fact]
    public async Task Handle_WithDeadline_UpdatesDeadline()
    {
        var goal = Goal.Create(UserId, "Title", 100, "km").Value;
        _goalRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(goal);

        var deadline = new DateOnly(2027, 6, 15);
        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 100, "km", deadline);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Deadline.Should().Be(deadline);
    }

    [Fact]
    public async Task Handle_PaywalledUser_ReturnsPayGateFailure()
    {
        _payGate.CanAccessGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Goals are a Pro feature"));

        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 100, "km", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DeadlineInPast_ReturnsFailureAndDoesNotLoadGoal()
    {
        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 100, "km", Today.AddDays(-1));

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.DeadlineInPast);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DeadlineToday_Updates()
    {
        var goal = Goal.Create(UserId, "Title", 100, "km").Value;
        SetupGoalFound(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "Title", null, 100, "km", Today);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Deadline.Should().Be(Today);
    }

    [Fact]
    public async Task Handle_TargetEditCompletesGoal_FiresGamificationAndWritesProgressLog()
    {
        var goal = Goal.Create(UserId, "Read", 100, "pages").Value;
        goal.UpdateProgress(80);
        SetupGoalFound(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "Read", null, 50, "pages", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Completed);
        await _progressLogRepo.Received(1).AddAsync(
            Arg.Is<GoalProgressLog>(l => l.Value == 80 && l.PreviousValue == 80),
            Arg.Any<CancellationToken>());
        await _gamificationService.Received(1).ProcessGoalCompleted(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TargetEditReopensCompletedGoal_NoGamificationOrProgressLog()
    {
        var goal = Goal.Create(UserId, "Read", 100, "pages").Value;
        goal.UpdateProgress(100);
        goal.Status.Should().Be(GoalStatus.Completed);
        SetupGoalFound(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "Read", null, 200, "pages", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Active);
        await _progressLogRepo.DidNotReceive().AddAsync(Arg.Any<GoalProgressLog>(), Arg.Any<CancellationToken>());
        await _gamificationService.DidNotReceive().ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TargetEditNoTransition_NoGamificationOrProgressLog()
    {
        var goal = Goal.Create(UserId, "Read", 100, "pages").Value;
        goal.UpdateProgress(40);
        SetupGoalFound(goal);

        var command = new UpdateGoalCommand(UserId, GoalId, "Read", null, 120, "pages", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Active);
        await _progressLogRepo.DidNotReceive().AddAsync(Arg.Any<GoalProgressLog>(), Arg.Any<CancellationToken>());
        await _gamificationService.DidNotReceive().ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TargetEditCompletes_GamificationThrows_StillSucceeds()
    {
        var goal = Goal.Create(UserId, "Read", 100, "pages").Value;
        goal.UpdateProgress(80);
        SetupGoalFound(goal);
        _gamificationService.ProcessGoalCompleted(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("gamification error"));

        var command = new UpdateGoalCommand(UserId, GoalId, "Read", null, 50, "pages", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        goal.Status.Should().Be(GoalStatus.Completed);
    }
}
