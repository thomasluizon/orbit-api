using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetRescheduleSuggestionQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IRescheduleSuggestionService _rescheduleService = Substitute.For<IRescheduleSuggestionService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GetRescheduleSuggestionQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid HabitId = Guid.NewGuid();
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    public GetRescheduleSuggestionQueryHandlerTests()
    {
        _handler = new GetRescheduleSuggestionQueryHandler(
            _habitRepo, _userRepo, _payGate, _rescheduleService, _cache);
    }

    private static User CreateTestUser() => User.Create("Test User", "test@example.com").Value;

    private static Habit CreateOverdueHabit() =>
        Habit.Create(new HabitCreateParams(
            UserId, "Read", FrequencyUnit: null, FrequencyQuantity: null,
            DueDate: Today.AddDays(-3))).Value;

    private static RescheduleSuggestion SampleSuggestion() =>
        new(FrequencyUnit.Day, 1, Today.AddDays(1), null, [], "Pick it back up tomorrow.");

    private void StubHabit(Habit habit) =>
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

    [Fact]
    public async Task Handle_GeneratesSuggestion_WhenNotCached()
    {
        _payGate.CanUseSmartReschedule(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateTestUser());
        StubHabit(CreateOverdueHabit());
        _rescheduleService.GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success(SampleSuggestion()));

        var result = await _handler.Handle(new GetRescheduleSuggestionQuery(UserId, HabitId, "en"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FromCache.Should().BeFalse();
        result.Value.Suggestion.Rationale.Should().Be("Pick it back up tomorrow.");
    }

    [Fact]
    public async Task Handle_ReturnsCachedSuggestion_OnSecondCall()
    {
        _payGate.CanUseSmartReschedule(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateTestUser());
        StubHabit(CreateOverdueHabit());
        _rescheduleService.GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success(SampleSuggestion()));

        var query = new GetRescheduleSuggestionQuery(UserId, HabitId, "en");
        await _handler.Handle(query, CancellationToken.None);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FromCache.Should().BeTrue();
        await _rescheduleService.Received(1).GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), "en", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PayGateFails_ReturnsFailure()
    {
        _payGate.CanUseSmartReschedule(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Smart reschedule is a Pro feature. Upgrade to unlock!"));

        var result = await _handler.Handle(new GetRescheduleSuggestionQuery(UserId, HabitId, "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(Result.PayGateErrorCode);
        await _rescheduleService.DidNotReceive().GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _payGate.CanUseSmartReschedule(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(new GetRescheduleSuggestionQuery(UserId, HabitId, "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_HabitNotFound_ReturnsFailure()
    {
        _payGate.CanUseSmartReschedule(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateTestUser());
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var result = await _handler.Handle(new GetRescheduleSuggestionQuery(UserId, HabitId, "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("HABIT_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_HabitNotOverdue_ReturnsFailure()
    {
        _payGate.CanUseSmartReschedule(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateTestUser());
        var dueTodayTask = Habit.Create(new HabitCreateParams(
            UserId, "Read", FrequencyUnit: null, FrequencyQuantity: null, DueDate: Today)).Value;
        StubHabit(dueTodayTask);

        var result = await _handler.Handle(new GetRescheduleSuggestionQuery(UserId, HabitId, "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("HABIT_NOT_OVERDUE");
        await _rescheduleService.DidNotReceive().GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ServiceFails_ReturnsFailure()
    {
        _payGate.CanUseSmartReschedule(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateTestUser());
        StubHabit(CreateOverdueHabit());
        _rescheduleService.GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RescheduleSuggestion>("AI reschedule temporarily unavailable"));

        var result = await _handler.Handle(new GetRescheduleSuggestionQuery(UserId, HabitId, "en"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("AI reschedule temporarily unavailable");
    }

    [Fact]
    public async Task Handle_UsesUserProfileLanguage_WhenSet()
    {
        var user = CreateTestUser();
        user.SetLanguage("pt-BR");
        _payGate.CanUseSmartReschedule(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        StubHabit(CreateOverdueHabit());
        _rescheduleService.GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), "pt-BR", Arg.Any<CancellationToken>())
            .Returns(Result.Success(SampleSuggestion()));

        var result = await _handler.Handle(new GetRescheduleSuggestionQuery(UserId, HabitId, "en"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _rescheduleService.Received(1).GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), "pt-BR", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_FallsBackToEnglish_WhenLanguagesEmpty()
    {
        _payGate.CanUseSmartReschedule(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(CreateTestUser());
        StubHabit(CreateOverdueHabit());
        _rescheduleService.GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), "en", Arg.Any<CancellationToken>())
            .Returns(Result.Success(SampleSuggestion()));

        var result = await _handler.Handle(new GetRescheduleSuggestionQuery(UserId, HabitId, ""), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _rescheduleService.Received(1).GenerateAsync(
            Arg.Any<Habit>(), Arg.Any<DateOnly>(), "en", Arg.Any<CancellationToken>());
    }
}
