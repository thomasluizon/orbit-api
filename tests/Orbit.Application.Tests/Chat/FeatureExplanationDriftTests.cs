using System.Linq.Expressions;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using FluentAssertions.Execution;
using NSubstitute;
using Orbit.Application.Chat.FeatureExplanations;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

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
    public void AllExplanations_UseProductLanguageWithoutInternalIdentifiers()
    {
        var internalKeys = new[] { typeof(FeatureFlagKeys), typeof(AppConfigKeys) }
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

        using var scope = new AssertionScope();
        foreach (var key in _service.Keys)
        {
            var body = Body(key);
            body.Should().NotMatchRegex(@"`[^`\r\n]+`",
                $"{key}.md must explain the product without backticked identifiers");
            foreach (var internalKey in internalKeys)
                body.Should().NotContain(internalKey,
                    $"{key}.md must not expose an internal flag or configuration key");
        }
    }

    [Fact]
    public void Freezes_MatchesStreakFreezeConstants()
    {
        var body = Body("freezes");

        body.Should().Contain(N(AppConstants.StreakDaysPerFreeze));
        body.Should().Contain(N(AppConstants.MaxStreakFreezesAccumulated));
        body.Should().Contain(N(AppConstants.MaxStreakFreezesPerMonth));
        body.Should().Contain("automatically spends");
        body.Should().Contain("manual repair");
    }

    [Fact]
    public void Streaks_MatchesLookbackConstant()
    {
        Body("streaks").Should().Contain(N(AppConstants.StreakRepairWindowDays));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paygate_QuotaClaimMatchesEnforcedDailyWindow(bool pro)
    {
        var user = User.Create("Quota reader", "quota@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        if (pro)
            user.GrantLifetimePro();
        var today = new DateOnly(2026, 8, 5);
        var users = Substitute.For<IGenericRepository<User>>();
        users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(user);
        var work = Substitute.For<IUnitOfWork>();
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<int>(1));
        var gate = new PayGateService(Substitute.For<IGenericRepository<Habit>>(), users, config, dates, Substitute.For<IFeatureFlagService>());
        var limit = pro ? AppConstants.DefaultProAiMessages : AppConstants.DefaultFreeAiMessages;

        for (var count = 0; count < limit; count++)
        {
            (await gate.TryConsumeAiMessage(user.Id, work)).IsSuccess.Should().BeTrue();
        }

        (await gate.TryConsumeAiMessage(user.Id, work)).IsFailure.Should().BeTrue();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(today.AddDays(1));
        (await gate.TryConsumeAiMessage(user.Id, work)).IsSuccess.Should().BeTrue();
        user.AiMessagesUsedToday.Should().Be(1);
        Body("paygate").Should().Contain(
            $"**AI messages** are capped at **{N(AppConstants.DefaultFreeAiMessages)}** per day. Pro raises this to **{N(AppConstants.DefaultProAiMessages)}** per day.",
            "paygate.md must describe the enforced daily quota and its plan limits");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paygate_ProUnlocksMatchEnforcedGates(bool pro)
    {
        var user = User.Create("Plan reader", "plan@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        if (pro)
            user.GrantLifetimePro();
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), true, Arg.Any<CancellationToken>()).Returns(true);
        var gate = new PayGateService(Substitute.For<IGenericRepository<Habit>>(), users, config,
            Substitute.For<IUserDateService>(), Substitute.For<IFeatureFlagService>());

        (await gate.CanUseDailySummary(user.Id)).IsSuccess.Should().Be(pro);
        (await gate.CanAccessCalendar(user.Id)).IsSuccess.Should().Be(pro);
        (await gate.CanUseRetrospective(user.Id)).IsSuccess.Should().Be(pro);

        var unlocks = Body("paygate").Split("## What Pro unlocks")[1]
            .Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal));
        unlocks.Should().BeEquivalentTo(
            ["- The daily AI summary", "- Calendar integration", "- Retrospectives"],
            "paygate.md must list the features these default plan gates enforce");
    }

    [Fact]
    public void ScheduleMath_MatchesOverdueWindowConstant()
    {
        Body("schedule-math").Should().Contain(N(AppConstants.DefaultOverdueWindowDays));
    }
}
