using System.Globalization;
using FluentAssertions;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Common;
using Orbit.Application.Gamification;

namespace Orbit.Application.Tests.Chat;

/// <summary>
/// Guards the pre-authored feature-explanation prose against drifting from the constants and
/// level table it documents. If a constant changes without the matching markdown, these fail.
/// </summary>
public class FeatureExplanationDriftTests
{
    private readonly FeatureExplanationService _service = new();

    private string Body(string key)
    {
        var explanation = _service.Get(key);
        explanation.Should().NotBeNull();
        return explanation!.Body;
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void Freezes_MatchesStreakFreezeConstants()
    {
        var body = Body("freezes");

        body.Should().Contain(N(AppConstants.StreakDaysPerFreeze));
        body.Should().Contain(N(AppConstants.MaxStreakFreezesAccumulated));
        body.Should().Contain(N(AppConstants.MaxStreakFreezesPerMonth));
    }

    [Fact]
    public void Streaks_MatchesLookbackConstant()
    {
        Body("streaks").Should().Contain(N(AppConstants.MaxStreakLookbackDays));
    }

    [Fact]
    public void Gamification_MatchesEveryLevelThresholdAndTitle()
    {
        var body = Body("gamification");

        foreach (var level in LevelDefinitions.All)
        {
            body.Should().Contain(N(level.XpRequired), $"level {level.Level} XP threshold should appear");
            body.Should().Contain(level.Title, $"level {level.Level} title should appear");
        }
    }

    [Fact]
    public void Paygate_MatchesPlanLimitConstants()
    {
        var body = Body("paygate");

        body.Should().Contain(N(AppConstants.DefaultFreeMaxHabits));
        body.Should().Contain(N(AppConstants.DefaultFreeAiMessages));
        body.Should().Contain(N(AppConstants.DefaultProAiMessages));
    }

    [Fact]
    public void AiMemory_MatchesMaxUserFactsConstant()
    {
        Body("ai-memory").Should().Contain(N(AppConstants.MaxUserFacts));
    }

    [Fact]
    public void ScheduleMath_MatchesOverdueWindowConstant()
    {
        Body("schedule-math").Should().Contain(N(AppConstants.DefaultOverdueWindowDays));
    }
}
