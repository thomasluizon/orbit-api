using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class UserDateServiceTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
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

        // Should return a valid date
        result.Should().NotBe(default);
    }

    [Fact]
    public async Task GetUserTodayAsync_UserWithoutTimezone_UsesUtc()
    {
        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _sut.GetUserTodayAsync(UserId);

        // Falls back to UTC
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

        // First call
        await _sut.GetUserTodayAsync(UserId);

        // Second call - should use cache
        await _sut.GetUserTodayAsync(UserId);

        // Only one call to repository expected
        await _userRepo.Received(1).GetByIdAsync(UserId, Arg.Any<CancellationToken>());
    }
}
