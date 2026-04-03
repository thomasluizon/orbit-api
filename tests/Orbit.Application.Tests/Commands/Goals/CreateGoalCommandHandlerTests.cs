using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Orbit.Application.Goals.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Goals;

public class CreateGoalCommandHandlerTests
{
    private readonly IGenericRepository<Goal> _goalRepo = Substitute.For<IGenericRepository<Goal>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly CreateGoalCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public CreateGoalCommandHandlerTests()
    {
        _handler = new CreateGoalCommandHandler(
            _goalRepo, _payGate, _gamificationService, _unitOfWork,
            Substitute.For<ILogger<CreateGoalCommandHandler>>());

        _payGate.CanCreateGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesGoalAndReturnsId()
    {
        var command = new CreateGoalCommand(UserId, "Run a marathon", "Train for 26.2 miles", 42.2m, "km", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(g => g.Title == "Run a marathon" && g.UserId == UserId),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithDeadline_CreatesGoalWithDeadline()
    {
        var deadline = new DateOnly(2026, 12, 31);
        var command = new CreateGoalCommand(UserId, "Learn piano", null, 100, "hours", deadline);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _goalRepo.Received(1).AddAsync(
            Arg.Is<Goal>(g => g.Title == "Learn piano" && g.Deadline == deadline),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PayGateLimitReached_ReturnsPayGateFailure()
    {
        _payGate.CanCreateGoals(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Goals are a Pro feature"));

        var command = new CreateGoalCommand(UserId, "New goal", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyTitle_ReturnsFailure()
    {
        var command = new CreateGoalCommand(UserId, "", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Title");
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ZeroTargetValue_ReturnsFailure()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 0, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value");
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_EmptyUnit_ReturnsFailure()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Unit");
        await _goalRepo.DidNotReceive().AddAsync(Arg.Any<Goal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_CallsGamificationProcessGoalCreated()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null);

        await _handler.Handle(command, CancellationToken.None);

        await _gamificationService.Received(1).ProcessGoalCreated(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GamificationThrows_StillReturnsSuccess()
    {
        _gamificationService.ProcessGoalCreated(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("gamification error"));

        var command = new CreateGoalCommand(UserId, "Goal", null, 10, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NegativeTargetValue_ReturnsFailure()
    {
        var command = new CreateGoalCommand(UserId, "Goal", null, -5, "units", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Target value");
    }
}
