using MediatR;
using NSubstitute;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Common;

namespace Orbit.Application.Tests.Chat;

internal static class GamificationReadTestData
{
    public static GamificationOverviewPayload Configure(IMediator mediator, DateOnly day)
    {
        AchievementDto[] achievements =
        [
            new("earned", "Earned", "Description", "habit", "common", 10, "star", true, DateTime.UnixEpoch),
            new("progress", "Progress", "Description", "habit", "rare", 20, "moon", false, null, 2, 10)
        ];
        var profile = new GamificationProfileResponse(120, 3, "Level", "level", 100, 200, 80,
            1, 2, achievements, [new("earned", DateTime.UnixEpoch)], 5, 9, day, true, false,
            new(4, "Next", 80, null));
        var catalogue = new AchievementsResponse(achievements);
        var streak = new StreakInfoResponse(5, 9, day, 1, 2, 3, true, [day], 2, 3, 2, 2, true, false, null, 0);
        mediator.Send(Arg.Any<GetGamificationProfileQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(profile));
        mediator.Send(Arg.Any<GetAchievementsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(catalogue));
        mediator.Send(Arg.Any<GetStreakInfoQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(streak));
        return new(profile, catalogue, streak);
    }
}
