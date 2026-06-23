using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class UserDateServiceTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IDistributedCache _cache = NewDistributedCache();
    private readonly UserDateService _sut;

    private static readonly Guid UserId = Guid.NewGuid();

    public UserDateServiceTests()
    {
        _sut = new UserDateService(_userRepo, _cache);
    }

    [Fact]
    public async Task GetUserTodayAsync_UserWithTimezone_ReturnsCorrectDate()
    {
        var user = User.Create("Test", "test@test.com").Value;
        user.SetTimeZone("America/New_York");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _sut.GetUserTodayAsync(UserId);

        result.Should().NotBe(default);
    }

    [Fact]
    public async Task GetUserTodayAsync_UserWithoutTimezone_UsesUtc()
    {
        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _sut.GetUserTodayAsync(UserId);

        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        result.Should().Be(utcToday);
    }

    [Fact]
    public async Task GetUserTodayAsync_UserNotFound_FallsBackToUtc()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _sut.GetUserTodayAsync(UserId);

        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        result.Should().Be(utcToday);
    }

    [Fact]
    public async Task GetUserTodayAsync_CachesTimezoneOnFirstCall()
    {
        var user = User.Create("Test", "test@test.com").Value;
        user.SetTimeZone("Europe/London");
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        await _sut.GetUserTodayAsync(UserId);

        await _sut.GetUserTodayAsync(UserId);

        await _userRepo.Received(1).GetByIdAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUserWeekStartDayAsync_AfterInvalidation_RereadsFromRepository()
    {
        var user = User.Create("Test", "test@test.com").Value;
        user.SetWeekStartDay(0);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        (await _sut.GetUserWeekStartDayAsync(UserId)).Should().Be(0);

        await _sut.InvalidateUserDatePreferencesAsync(UserId);
        await _sut.GetUserWeekStartDayAsync(UserId);

        await _userRepo.Received(2).GetByIdAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetUserWeekStartDayAsync_SecondInstanceSharingCache_ReadsCachedValueWithoutRepository()
    {
        var user = User.Create("Test", "test@test.com").Value;
        user.SetWeekStartDay(0);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        await _sut.GetUserWeekStartDayAsync(UserId);

        var secondInstanceRepo = Substitute.For<IGenericRepository<User>>();
        var secondInstance = new UserDateService(secondInstanceRepo, _cache);

        var result = await secondInstance.GetUserWeekStartDayAsync(UserId);

        result.Should().Be(0);
        await secondInstanceRepo.DidNotReceive().GetByIdAsync(UserId, Arg.Any<CancellationToken>());
    }

    private static IDistributedCache NewDistributedCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
}
