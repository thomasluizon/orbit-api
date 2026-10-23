using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat;

public class RemainingHabitScheduleReaderTests
{
    private static readonly DateOnly Today = new(2026, 4, 3);

    [Theory]
    [InlineData(HabitSchedule.OneTime, false)]
    [InlineData(HabitSchedule.OneTime, true)]
    [InlineData(HabitSchedule.Recurring, false)]
    [InlineData(HabitSchedule.Recurring, true)]
    [InlineData(HabitSchedule.Flexible, false)]
    [InlineData(HabitSchedule.Flexible, true)]
    [InlineData(HabitSchedule.General, false)]
    [InlineData(HabitSchedule.General, true)]
    public void FieldSummary_KeepsFlexibleLabel(HabitSchedule schedule, bool divergentFlags)
    {
        var summary = HabitFieldSummary.From(Create(schedule, divergentFlags));

        summary.IsFlexible.Should().Be(schedule == HabitSchedule.Flexible);
        summary.Format().Contains("Flexible: true").Should().Be(schedule == HabitSchedule.Flexible);
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime, false)]
    [InlineData(HabitSchedule.OneTime, true)]
    [InlineData(HabitSchedule.Recurring, false)]
    [InlineData(HabitSchedule.Recurring, true)]
    [InlineData(HabitSchedule.Flexible, false)]
    [InlineData(HabitSchedule.Flexible, true)]
    [InlineData(HabitSchedule.General, false)]
    [InlineData(HabitSchedule.General, true)]
    public void CardsAndTodayFacts_KeepCompletionAfterCursorAdvances(HabitSchedule schedule, bool divergentFlags)
    {
        var habit = Create(schedule, divergentFlags);
        var before = HabitListCardBuilder.Build([habit], Today, "all", supportsDoneStatus: true);
        before.Items.Should().ContainSingle().Which.Status.Should().Be(schedule == HabitSchedule.General ? "general" : "today");
        habit.Log(Today).IsSuccess.Should().BeTrue();

        var facts = HabitTodaySnapshot.FromLoadedHabits([habit], Today);

        facts.TodayIds.Contains(habit.Id).Should().Be(schedule != HabitSchedule.General);
        facts.OverdueIds.Should().BeEmpty();
        facts.DoneTodayIds.Should().Contain(habit.Id);
        HabitListCardBuilder.Build([habit], Today, "all", supportsDoneStatus: true)
            .Items.Should().ContainSingle().Which.Status.Should().Be("done");
        HabitListCardBuilder.Build([habit], Today, "all").Items.Should().ContainSingle().Which.Status
            .Should().Be(schedule == HabitSchedule.OneTime ? "none" : schedule == HabitSchedule.General ? "general"
                : schedule == HabitSchedule.Recurring ? "none" : "today");
        habit.StartDate.Should().Be(Today);
        habit.NextDueDate.Should().Be(schedule == HabitSchedule.Recurring ? Today.AddDays(1) : Today);
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime, false)]
    [InlineData(HabitSchedule.OneTime, true)]
    [InlineData(HabitSchedule.Recurring, false)]
    [InlineData(HabitSchedule.Recurring, true)]
    [InlineData(HabitSchedule.Flexible, false)]
    [InlineData(HabitSchedule.Flexible, true)]
    [InlineData(HabitSchedule.General, false)]
    [InlineData(HabitSchedule.General, true)]
    public void Correction_KeepsScheduleAndRejectsUnrequestedTypeChange(HabitSchedule schedule, bool divergentFlags)
    {
        var habit = Create(schedule, divergentFlags);
        var validator = new HabitCorrectionProposalValidator();
        var same = JsonSerializer.SerializeToElement(new
        {
            title = "Renamed",
            is_general = schedule == HabitSchedule.General,
            is_flexible = schedule == HabitSchedule.Flexible
        });
        validator.Validate(new HabitCorrectionProposal("Rename it", same, habit)).IsValid.Should().BeTrue();
        var changed = JsonSerializer.SerializeToElement(new { is_general = schedule != HabitSchedule.General });
        validator.Validate(new HabitCorrectionProposal("Rename it", changed, habit)).Errors
            .Should().Contain(error => error.ErrorCode == "CORRECTION_PRESERVES_SCHEDULE");
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime, false)]
    [InlineData(HabitSchedule.OneTime, true)]
    [InlineData(HabitSchedule.Recurring, false)]
    [InlineData(HabitSchedule.Recurring, true)]
    [InlineData(HabitSchedule.Flexible, false)]
    [InlineData(HabitSchedule.Flexible, true)]
    [InlineData(HabitSchedule.General, false)]
    [InlineData(HabitSchedule.General, true)]
    public void PerfectAchievements_KeepGeneralAndCompletedTaskExclusions(HabitSchedule schedule, bool divergentFlags)
    {
        var habit = Create(schedule, divergentFlags);
        var user = User.Create("Person", "person@example.com").Value;
        var earned = new HashSet<string>();
        var awards = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        habit.Log(Today, advanceDueDate: false).IsSuccess.Should().BeTrue();

        AchievementChecks.CheckPerfectDay([habit], Today, earned, user, awards);
        AchievementChecks.CheckPerfectWeekAndMonth([habit], Today, earned, user, awards);

        earned.Should().BeEquivalentTo(schedule is HabitSchedule.Recurring or HabitSchedule.Flexible
            ? new[] { AchievementDefinitions.PerfectDay, AchievementDefinitions.PerfectWeek, AchievementDefinitions.PerfectMonth }
            : Array.Empty<string>());
    }

    [Theory]
    [InlineData(HabitSchedule.OneTime, false)]
    [InlineData(HabitSchedule.OneTime, true)]
    [InlineData(HabitSchedule.Recurring, false)]
    [InlineData(HabitSchedule.Recurring, true)]
    [InlineData(HabitSchedule.Flexible, false)]
    [InlineData(HabitSchedule.Flexible, true)]
    [InlineData(HabitSchedule.General, false)]
    [InlineData(HabitSchedule.General, true)]
    public async Task QueryTool_KeepsGeneralFilterAndTypedStatus(HabitSchedule schedule, bool divergentFlags)
    {
        var habit = Create(schedule, divergentFlags);
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { habit }.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        var users = Substitute.For<IGenericRepository<User>>();
        users.GetByIdAsync(habit.UserId, Arg.Any<CancellationToken>()).Returns(User.Create("Person", "person@example.com").Value);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(habit.UserId, Arg.Any<CancellationToken>()).Returns(Today);
        var tool = new QueryHabitsTool(habits, users, dates, Substitute.For<IHabitScheduleLogReader>());
        foreach (var generalFilter in new[] { true, false })
        {
            var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { is_general = generalFilter }), habit.UserId, default);
            result.Success.Should().BeTrue();
            var card = result.Payload.Should().BeOfType<HabitListCard>().Subject;
            card.Items.Count.Should().Be(generalFilter == (schedule == HabitSchedule.General) ? 1 : 0);
            if (card.Items.Count > 0)
                card.Items[0].Status.Should().Be(schedule == HabitSchedule.General ? "general" : "today");
        }
    }

    private static Habit Create(HabitSchedule schedule, bool divergentFlags)
    {
        var habit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Habit",
            schedule is HabitSchedule.Recurring or HabitSchedule.Flexible ? FrequencyUnit.Day : null,
            schedule == HabitSchedule.Flexible ? 2 : schedule == HabitSchedule.Recurring ? 1 : null,
            Today, IsGeneral: schedule == HabitSchedule.General, IsFlexible: schedule == HabitSchedule.Flexible,
            CreatedAtUtc: Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Value;
        if (divergentFlags)
        {
            typeof(Habit).GetProperty(nameof(Habit.IsGeneral))!.SetValue(habit, !habit.IsGeneral);
            typeof(Habit).GetProperty(nameof(Habit.IsFlexible))!.SetValue(habit, !habit.IsFlexible);
        }
        return habit;
    }
}
