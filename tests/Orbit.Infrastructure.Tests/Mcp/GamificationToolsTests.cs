using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;

namespace Orbit.Infrastructure.Tests.Mcp;

public class GamificationToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly GamificationTools _tools;
    private readonly ClaimsPrincipal _user;

    public GamificationToolsTests()
    {
        _tools = new GamificationTools(_mediator);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task GetGamificationProfile_Success_ReturnsFormattedProfile()
    {
        var profile = new GamificationProfileResponse(
            1500, 5, "Achiever", 1000, 2000, 500,
            3, 10, [], [],
            15, 20, new DateOnly(2026, 4, 2));

        _mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(profile));

        var result = await _tools.GetGamificationProfile(_user);

        result.Should().Contain("Level: 5 (Achiever)");
        result.Should().Contain("Total XP: 1500");
        result.Should().Contain("Current Streak: 15 days");
        result.Should().Contain("Achievements: 3/10");
    }

    [Fact]
    public async Task GetGamificationProfile_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GamificationProfileResponse>("Pro required"));

        var result = await _tools.GetGamificationProfile(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetAchievements_Success_WithEarnedAndLocked_ReturnsFormattedList()
    {
        var achievements = new List<AchievementDto>
        {
            new("first-habit", "First Habit", "Create your first habit", "Beginner", "Common", 50, "star", true, DateTime.UtcNow),
            new("streak-7", "Week Streak", "7-day streak", "Consistency", "Rare", 100, "fire", false, null)
        };
        var response = new AchievementsResponse(achievements);

        _mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.GetAchievements(_user);

        result.Should().Contain("Earned (1)");
        result.Should().Contain("Locked (1)");
        result.Should().Contain("First Habit");
        result.Should().Contain("Week Streak");
    }

    [Fact]
    public async Task GetAchievements_Empty_ReturnsNoAchievementsMessage()
    {
        var response = new AchievementsResponse([]);
        _mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.GetAchievements(_user);

        result.Should().Contain("No achievements available");
    }

    [Fact]
    public async Task GetAchievements_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AchievementsResponse>("Pro required"));

        var result = await _tools.GetAchievements(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetStreakInfo_Success_ReturnsFormattedStreak()
    {
        var streak = new StreakInfoResponse(
            15, 30, new DateOnly(2026, 4, 2),
            1, 1, 2, false,
            [new DateOnly(2026, 3, 15)],
            1, 3, 7, 1, true);

        _mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(streak));

        var result = await _tools.GetStreakInfo(_user);

        result.Should().Contain("Current Streak: 15 days");
        result.Should().Contain("Longest Streak: 30 days");
        result.Should().Contain("Freezes Available: 1/2");
    }

    [Fact]
    public async Task GetStreakInfo_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<StreakInfoResponse>("Not found"));

        var result = await _tools.GetStreakInfo(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task ActivateStreakFreeze_Success_ReturnsActivatedMessage()
    {
        var response = new StreakFreezeResponse(1, new DateOnly(2026, 4, 3), 15, 0);
        _mediator.Send(Arg.Any<ActivateStreakFreezeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.ActivateStreakFreeze(_user);

        result.Should().Contain("Streak freeze activated");
        result.Should().Contain("streak preserved: 15 days");
        result.Should().Contain("Freezes remaining this month: 1");
    }

    [Fact]
    public async Task ActivateStreakFreeze_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<ActivateStreakFreezeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<StreakFreezeResponse>("No freezes remaining"));

        var result = await _tools.ActivateStreakFreeze(_user);

        result.Should().StartWith("Error: ");
    }
}
