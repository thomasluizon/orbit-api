using FluentAssertions;
using NSubstitute;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Commands;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Gamification;

public class ReportEventCommandHandlerTests
{
    private readonly IGamificationService _gamificationService = Substitute.For<IGamificationService>();
    private readonly ReportEventCommandHandler _sut;
    private static readonly Guid UserId = Guid.NewGuid();

    public ReportEventCommandHandlerTests()
    {
        _sut = new ReportEventCommandHandler(_gamificationService);
    }

    [Fact]
    public async Task Handle_CardSharedKey_GrantsShowOffAndReturnsSummary()
    {
        _gamificationService.TryGrantAchievementsAsync(
            UserId,
            Arg.Is<IReadOnlyList<string>>(ids => ids.Single() == AchievementDefinitions.ShowOff),
            Arg.Any<CancellationToken>())
            .Returns(new List<string> { AchievementDefinitions.ShowOff });

        var result = await _sut.Handle(new ReportEventCommand(UserId, AchievementEventMap.CardShared), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Granted.Should().ContainSingle();
        result.Value.Granted[0].Id.Should().Be(AchievementDefinitions.ShowOff);
        result.Value.Granted[0].IsEarned.Should().BeTrue();
        result.Value.Granted[0].XpReward.Should().Be(75);
    }

    [Fact]
    public async Task Handle_WrappedViewedKey_MapsToYearInReview()
    {
        _gamificationService.TryGrantAchievementsAsync(
            UserId,
            Arg.Is<IReadOnlyList<string>>(ids => ids.Single() == AchievementDefinitions.YearInReview),
            Arg.Any<CancellationToken>())
            .Returns(new List<string> { AchievementDefinitions.YearInReview });

        var result = await _sut.Handle(new ReportEventCommand(UserId, AchievementEventMap.WrappedViewed), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Granted.Should().ContainSingle();
        result.Value.Granted[0].Id.Should().Be(AchievementDefinitions.YearInReview);
    }

    [Fact]
    public async Task Handle_AlreadyEarned_ReturnsEmptyGranted()
    {
        _gamificationService.TryGrantAchievementsAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        var result = await _sut.Handle(new ReportEventCommand(UserId, AchievementEventMap.CardShared), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Granted.Should().BeEmpty();
    }
}
