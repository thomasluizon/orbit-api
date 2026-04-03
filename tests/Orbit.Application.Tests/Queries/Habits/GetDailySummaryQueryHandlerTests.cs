using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Habits;

public class GetDailySummaryQueryHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly ISummaryService _summaryService = Substitute.For<ISummaryService>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly GetDailySummaryQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public GetDailySummaryQueryHandlerTests()
    {
        _handler = new GetDailySummaryQueryHandler(_habitRepo, _userRepo, _payGate, _summaryService, _cache);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_GeneratesNewSummary_WhenNotCached()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Today, Today, false, "en",
            Arg.Any<CancellationToken>())
            .Returns(Result.Success("Test summary content"));

        var query = new GetDailySummaryQuery(UserId, Today, Today, false, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Summary.Should().Be("Test summary content");
        result.Value.FromCache.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ReturnsCachedSummary_WhenCached()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Today, Today, false, "en",
            Arg.Any<CancellationToken>())
            .Returns(Result.Success("First call summary"));

        var query = new GetDailySummaryQuery(UserId, Today, Today, false, "en");

        // First call populates cache
        await _handler.Handle(query, CancellationToken.None);

        // Second call should return from cache
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.FromCache.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetDailySummaryQuery(UserId, Today, Today, false, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_PayGateFails_ReturnsFailure()
    {
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>())
            .Returns(Result.Failure("PAY_GATE", "PAY_GATE"));

        var query = new GetDailySummaryQuery(UserId, Today, Today, false, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_AiSummaryDisabled_ReturnsFailure()
    {
        var user = CreateTestUser();
        user.SetAiSummary(false);

        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        var query = new GetDailySummaryQuery(UserId, Today, Today, false, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("AI summary is disabled");
    }

    [Fact]
    public async Task Handle_SummaryServiceFails_ReturnsFailure()
    {
        var user = CreateTestUser();
        _payGate.CanUseDailySummary(UserId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        _summaryService.GenerateSummaryAsync(
            Arg.Any<IEnumerable<Habit>>(),
            Today, Today, false, "en",
            Arg.Any<CancellationToken>())
            .Returns(Result.Failure<string>("AI service unavailable"));

        var query = new GetDailySummaryQuery(UserId, Today, Today, false, "en");

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("AI service unavailable");
    }
}
