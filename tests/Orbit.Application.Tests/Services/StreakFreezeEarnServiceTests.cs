using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification.Services;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Services;

public class StreakFreezeEarnServiceTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly StreakFreezeEarnService _service;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 15);

    public StreakFreezeEarnServiceTests()
    {
        _service = new StreakFreezeEarnService(_userRepo);
    }

    [Fact]
    public async Task EvaluateAsync_UserNotFound_ReturnsZeroOutcome()
    {
        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var outcome = await _service.EvaluateAsync(UserId);

        outcome.FreezesEarned.Should().Be(0);
        outcome.NewBalance.Should().Be(0);
    }

    [Fact]
    public async Task EvaluateAsync_Streak7_GrantsOneFreeze()
    {
        var user = User.Create("Test", "test@test.com").Value;
        user.SetStreakState(7, 7, Today);

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var outcome = await _service.EvaluateAsync(UserId);

        outcome.FreezesEarned.Should().Be(1);
        outcome.NewBalance.Should().Be(1);
        user.StreakFreezeBalance.Should().Be(1);
    }

    [Fact]
    public async Task EvaluateAsync_NoEligible_ReturnsZeroOutcome_DoesNotMutate()
    {
        var user = User.Create("Test", "test@test.com").Value;
        user.SetStreakState(3, 3, Today);

        _userRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(),
            Arg.Any<CancellationToken>())
            .Returns(user);

        var outcome = await _service.EvaluateAsync(UserId);

        outcome.FreezesEarned.Should().Be(0);
        user.StreakFreezeBalance.Should().Be(0);
    }
}
