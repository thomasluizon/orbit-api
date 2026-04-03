using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetRetrospectiveQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IRetrospectiveService _retroService = Substitute.For<IRetrospectiveService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GetRetrospectiveQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly DateFrom = new(2026, 3, 1);
    private static readonly DateOnly DateTo = new(2026, 3, 31);

    public GetRetrospectiveQueryHandlerTests()
    {
        _handler = new GetRetrospectiveQueryHandler(_habitRepo, _payGate, _retroService, _cache);
    }

    private static Habit CreateTestHabit()
    {
        return Habit.Create(new HabitCreateParams(
            UserId, "Test Habit", FrequencyUnit.Day, 1,
            DueDate: DateFrom)).Value;
    }

    [Fact]
    public async Task Handle_GeneratesNewRetrospective_WhenNotCached()
    {
        _payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var habit = CreateTestHabit();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        _retroService.GenerateRetrospectiveAsync(
            Arg.Any<List<Habit>>(),
            DateFrom, DateTo, "weekly", "en",
            Arg.Any<CancellationToken>())
            .Returns(Result.Success("Retrospective content"));

        var query = new GetRetrospectiveQuery(UserId, DateFrom, DateTo, "weekly", "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Retrospective.Should().Be("Retrospective content");
        result.Value.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ReturnsCachedResult_WhenCached()
    {
        _payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var habit = CreateTestHabit();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        _retroService.GenerateRetrospectiveAsync(
            Arg.Any<List<Habit>>(),
            DateFrom, DateTo, "weekly", "en",
            Arg.Any<CancellationToken>())
            .Returns(Result.Success("Retro content"));

        var query = new GetRetrospectiveQuery(UserId, DateFrom, DateTo, "weekly", "en");

        // First call populates cache
        await _handler.Handle(query, CancellationToken.None);

        // Second call should return from cache
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_PayGateFails_ReturnsFailure()
    {
        _payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("PAY_GATE", "PAY_GATE"));

        var query = new GetRetrospectiveQuery(UserId, DateFrom, DateTo, "weekly", "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_NoHabits_ReturnsFailure()
    {
        _payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var query = new GetRetrospectiveQuery(UserId, DateFrom, DateTo, "weekly", "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("No habits found");
    }

    [Fact]
    public async Task Handle_RetrospectiveServiceFails_ReturnsFailure()
    {
        _payGate.CanUseRetrospective(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var habit = CreateTestHabit();
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { habit }.AsReadOnly());

        _retroService.GenerateRetrospectiveAsync(
            Arg.Any<List<Habit>>(),
            DateFrom, DateTo, "weekly", "en",
            Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("AI service error"));

        var query = new GetRetrospectiveQuery(UserId, DateFrom, DateTo, "weekly", "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("AI service error");
    }
}
