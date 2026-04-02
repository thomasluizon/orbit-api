using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Commands.Habits;

public class CreateSubHabitCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Tag> _tagRepo = Substitute.For<IGenericRepository<Tag>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly IAppConfigService _appConfigService = Substitute.For<IAppConfigService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly CreateSubHabitCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public CreateSubHabitCommandHandlerTests()
    {
        _handler = new CreateSubHabitCommandHandler(
            _habitRepo, _tagRepo, _payGate, _userDateService, _unitOfWork, _appConfigService, _cache);

        _payGate.CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _appConfigService.GetAsync("MaxHabitDepth", 5, Arg.Any<CancellationToken>())
            .Returns(5);
    }

    private static Habit CreateParentHabit()
    {
        return Habit.Create(
            UserId, "Parent Habit", FrequencyUnit.Day, 1,
            dueDate: Today).Value;
    }

    [Fact]
    public async Task Handle_ValidCommand_CreatesChild()
    {
        var parent = CreateParentHabit();
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(parent);
        // GetDepthAsync loads all user habits via FindAsync (parent has no parent = depth 0)
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { parent }.AsReadOnly());

        var command = new CreateSubHabitCommand(UserId, parent.Id, "Child Task", "Do this");

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeEmpty();
        await _habitRepo.Received(1).AddAsync(
            Arg.Is<Habit>(h => h.Title == "Child Task"),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ParentNotFound_ReturnsFailure()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((Habit?)null);

        var command = new CreateSubHabitCommand(UserId, Guid.NewGuid(), "Child", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Parent habit not found.");
    }

    [Fact]
    public async Task Handle_PayGated_ReturnsPayGateFailure()
    {
        _payGate.CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Sub-habits are a Pro feature"));

        var command = new CreateSubHabitCommand(UserId, Guid.NewGuid(), "Child", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_ExceedsMaxDepth_ReturnsFailure()
    {
        _appConfigService.GetAsync("MaxHabitDepth", 5, Arg.Any<CancellationToken>())
            .Returns(2);

        // Create a chain: grandparent -> parent (depth = 1, maxDepth - 1 = 1, so blocked)
        var grandparent = CreateParentHabit();
        var parent = Habit.Create(
            UserId, "Child", FrequencyUnit.Day, 1,
            dueDate: Today, parentHabitId: grandparent.Id).Value;

        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(parent);

        // GetDepthAsync now loads ALL user habits via FindAsync and walks in memory
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { grandparent, parent }.AsReadOnly());

        var command = new CreateSubHabitCommand(UserId, parent.Id, "Grandchild", null);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Maximum nesting depth");
    }
}
