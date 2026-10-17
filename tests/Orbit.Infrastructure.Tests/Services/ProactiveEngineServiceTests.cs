using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ProactiveEngineServiceTests
{
    private static readonly DateOnly Today = new(2030, 10, 14);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Status_ActiveReadPreservesExposureAndEngagement(bool shown, bool opened)
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        var item = await h.Db.ProactiveItems.SingleAsync();
        if (shown) await h.Engine.GetCurrentAsync(h.User.Id, default);
        if (opened) await h.Engine.RecordOutcomeAsync(h.User.Id, item.Id, false, default);

        await AssertStatusReadOnly(h, item.Id, true);
    }

    [Fact]
    public async Task Status_ShownGoalStaysActiveWhenEveningCheckinTakesPriority()
    {
        using var h = new Harness(17);
        h.SeedTrigger(ProactiveTrigger.GoalBehind);
        await h.Run();
        var goalItem = await h.Engine.GetCurrentAsync(h.User.Id, default);
        goalItem!.Trigger.Should().Be(ProactiveTrigger.GoalBehind);
        h.AtHour(19);
        h.Db.Habits.Add(HabitFor(h.User.Id));
        await h.Run();
        var current = await h.Engine.GetCurrentAsync(h.User.Id, default);
        current!.Trigger.Should().Be(ProactiveTrigger.EveningCheckin);
        current.Id.Should().NotBe(goalItem.Id);

        await AssertStatusReadOnly(h, goalItem.Id, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_ResolvedConditionEndsShownItemWithoutCreditingAnAction(bool goalItem)
    {
        using var h = new Harness(19);
        h.SeedTrigger(goalItem ? ProactiveTrigger.GoalBehind : ProactiveTrigger.EveningCheckin);
        await h.Run();
        var item = await h.Engine.GetCurrentAsync(h.User.Id, default);
        item.Should().NotBeNull();
        if (goalItem)
        {
            var goal = await h.Db.Goals.SingleAsync();
            goal.UpdateProgress(goal.TargetValue).IsSuccess.Should().BeTrue();
        }
        else
        {
            var habit = await h.Db.Habits.SingleAsync();
            var logged = habit.Log(Today, advanceDueDate: false);
            logged.IsSuccess.Should().BeTrue();
            h.Db.HabitLogs.Add(logged.Value);
        }
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await AssertStatusReadOnly(h, item!.Id, false);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(43)]
    public async Task Status_WindowClosedAndDayEndedAreReadWithoutSavingAnOutcome(int hours)
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        var item = await h.Engine.GetCurrentAsync(h.User.Id, default);
        h.Clock.Now = Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(hours);

        await AssertStatusReadOnly(h, item!.Id, false);
    }

    [Fact]
    public async Task Status_ActedItemStaysEndedWhileItsConditionIsTrue()
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        var item = await h.Engine.GetCurrentAsync(h.User.Id, default);
        await h.Engine.RecordOutcomeAsync(h.User.Id, item!.Id, true, default);
        h.AtHour(20);

        await AssertStatusReadOnly(h, item.Id, false);
    }

    [Fact]
    public async Task Status_PersistedEndIsFinalEvenIfTheConditionReturns()
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        var item = await h.Db.ProactiveItems.SingleAsync();
        item.End(ProactiveEndReason.ConditionResolved, h.Clock.Now);
        await h.Db.SaveChangesAsync();

        await AssertStatusReadOnly(h, item.Id, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_MissingAndForeignItemsAreUnavailable(bool foreignItem)
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        var item = await h.Db.ProactiveItems.SingleAsync();

        await AssertStatusReadOnly(h, foreignItem ? item.Id : Guid.NewGuid(), null,
            foreignItem ? Guid.NewGuid() : h.User.Id);
    }

    [Theory]
    [InlineData("2030-10-15T00:30:00Z", true)]
    [InlineData("2030-10-15T01:00:00Z", false)]
    [InlineData("2030-10-15T22:00:00Z", false)]
    public async Task Status_UsesOwnersLocalWindowAndDayAcrossUtcMidnight(string utc, bool active)
    {
        using var h = new Harness(22);
        h.User.SetTimeZone("America/Sao_Paulo");
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        var item = await h.Engine.GetCurrentAsync(h.User.Id, default);
        item.Should().NotBeNull();
        h.Clock.Now = DateTime.Parse(utc, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

        await AssertStatusReadOnly(h, item!.Id, active);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Status_IneligibleOwnerOrKillSwitchEndsActionability(bool disabled)
    {
        using var h = new Harness(19, lifetime: disabled);
        if (!disabled) h.User.StartTrial(h.Clock.Now.AddMinutes(1));
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        var item = await h.Engine.GetCurrentAsync(h.User.Id, default);
        item.Should().NotBeNull();
        if (disabled)
        {
            h.Db.AppFeatureFlags.Add(AppFeatureFlag.Create(FeatureFlagKeys.AstraProactiveEngineDisabled, true));
            await h.Db.SaveChangesAsync();
        }
        h.Clock.Now = h.Clock.Now.AddMinutes(2);

        await AssertStatusReadOnly(h, item!.Id, false);
    }

    private static async Task AssertStatusReadOnly(Harness h, Guid itemId, bool? expected, Guid? userId = null)
    {
        var before = await h.Db.ProactiveItems.AsNoTracking().ToListAsync();
        h.Analytics.ClearReceivedCalls();
        h.Push.ClearReceivedCalls();

        (await h.Engine.IsItemActiveAsync(userId ?? h.User.Id, itemId, default)).Should().Be(expected);

        var after = await h.Db.ProactiveItems.AsNoTracking().ToListAsync();
        after.Should().BeEquivalentTo(before);
        h.Db.ChangeTracker.HasChanges().Should().BeFalse();
        h.Analytics.ReceivedCalls().Should().BeEmpty();
        h.Push.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(ProactiveTrigger.RestartAfterMiss, 8, 7, 12)]
    [InlineData(ProactiveTrigger.EveningCheckin, 19, 17, 22)]
    [InlineData(ProactiveTrigger.BusyCalendarDay, 8, 7, 11)]
    [InlineData(ProactiveTrigger.GoalBehind, 19, 8, 21)]
    [InlineData(ProactiveTrigger.WeeklyReview, 8, 7, 21)]
    [InlineData(ProactiveTrigger.MorningPlan, 6, 5, 12)]
    public async Task EachTrigger_RequiresItsConditionAndWindow(ProactiveTrigger trigger, int inside, int before, int after)
    {
        foreach (var (hour, condition, expected) in new[]
        {
            (inside, true, true), (before, true, false), (after, true, false), (inside, false, false)
        })
        {
            using var h = new Harness(hour);
            if (condition) h.SeedTrigger(trigger);
            await h.Run();
            h.Db.ProactiveItems.Any(i => i.Trigger == trigger).Should().Be(expected, $"{trigger} at {hour} with condition {condition}");
            if (expected && trigger == ProactiveTrigger.RestartAfterMiss)
            {
                var item = h.Db.ProactiveItems.Single(i => i.Trigger == trigger);
                using var facts = JsonDocument.Parse(item.FactsJson);
                facts.RootElement.GetProperty("HadScheduledHabitsYesterday").GetBoolean().Should().BeTrue();
                facts.RootElement.GetProperty("HadPositiveLogsYesterday").GetBoolean().Should().BeFalse();
                facts.RootElement.GetProperty("HadRecentActivity").GetBoolean().Should().BeTrue();
            }
            if (!ProactiveRules.MayPush(trigger))
                h.Db.ProactiveItems.Any(i => i.Trigger == trigger && i.PushedAtUtc.HasValue).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Current_UsesPriorityAndCreditsLoggingFromAnotherSurface()
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        h.SeedTrigger(ProactiveTrigger.GoalBehind);
        await h.Run();
        var current = await h.Engine.GetCurrentAsync(h.User.Id, default);
        current!.Trigger.Should().Be(ProactiveTrigger.EveningCheckin);
        var shown = current.ShownAtUtc;
        (await h.Engine.GetCurrentAsync(h.User.Id, default))!.ShownAtUtc.Should().Be(shown);
        var habit = await h.Db.Habits.SingleAsync();
        var logged = habit.Log(Today, advanceDueDate: false);
        logged.IsSuccess.Should().BeTrue();
        h.Db.HabitLogs.Add(logged.Value);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        (await h.Engine.GetCurrentAsync(h.User.Id, default))!.Trigger.Should().Be(ProactiveTrigger.GoalBehind);
        var evening = await h.Db.ProactiveItems.SingleAsync(i => i.Trigger == ProactiveTrigger.EveningCheckin);
        evening.ActedAtUtc.Should().NotBeNull();
        evening.EndReason.Should().Be(ProactiveEndReason.ConditionResolved);
    }

    [Fact]
    public async Task CalendarMorningAndEvening_OnlyMorningPushes_AndCalendarIsReadOnce()
    {
        using var h = new Harness(8);
        h.SeedTrigger(ProactiveTrigger.BusyCalendarDay);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        h.AtHour(9);
        await h.Run();
        h.AtHour(18);
        await h.Run();
        var items = await h.Db.ProactiveItems.ToListAsync();
        items.Single(i => i.Trigger == ProactiveTrigger.BusyCalendarDay).PushedAtUtc.Should().NotBeNull();
        items.Single(i => i.Trigger == ProactiveTrigger.EveningCheckin).PushedAtUtc.Should().BeNull();
        await h.Push.Received(1).SendToUserAsync(h.User.Id, "Astra", Arg.Any<string>(), "/chat", Arg.Any<CancellationToken>());
        await h.Calendar.Received(1).FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), null, Arg.Any<CancellationToken>());
        h.Db.Notifications.Count().Should().Be(1);
    }

    [Fact]
    public async Task BusyCalendar_DurationThresholdAndAllDayExclusion()
    {
        using var h = new Harness(8);
        h.SeedTrigger(ProactiveTrigger.BusyCalendarDay);
        h.Calendar.FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), null, Arg.Any<CancellationToken>())
            .Returns([Event(8, 12)]);
        await h.Run();
        h.Db.ProactiveItems.Any(i => i.Trigger == ProactiveTrigger.BusyCalendarDay).Should().BeTrue();
        ProactiveEngineService.TimedRanges([Event(8, 12) with { StartTime = null }], Today, TimeZoneInfo.Utc).Should().BeEmpty();
    }

    [Fact]
    public void BusyCalendar_DurationCountsElapsedHoursAcrossDst()
    {
        var day = new DateOnly(2030, 11, 3);
        var item = Event(5, 9, day);
        var ranges = ProactiveEngineService.TimedRanges([item], day,
            TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        ranges.Should().ContainSingle();
        (ranges[0].End - ranges[0].Start).TotalHours.Should().Be(4);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WeeklyReview_IncludesWeeksWithOnlyBadOrChildHabits(bool badHabit)
    {
        using var h = new Harness(8);
        h.User.SetWeekStartDay(1);
        Guid? parentId = null;
        if (!badHabit)
        {
            var parent = Habit.Create(new HabitCreateParams(h.User.Id, "Routine", FrequencyUnit.Day, 1, Today.AddDays(1))).Value;
            h.Db.Habits.Add(parent);
            parentId = parent.Id;
        }
        var habit = Habit.Create(new HabitCreateParams(h.User.Id, "Read", FrequencyUnit.Day, 1,
            Today.AddDays(-20), IsBadHabit: badHabit, ParentHabitId: parentId,
            CreatedAtUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))).Value;
        h.Db.Habits.Add(habit);
        await h.Run();
        h.Db.ProactiveItems.Any(i => i.Trigger == ProactiveTrigger.WeeklyReview).Should().BeTrue();
        h.Db.ProactiveItems.Any(i => i.Trigger == ProactiveTrigger.MorningPlan).Should().Be(!badHabit);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(19)]
    public async Task BadHabitOnly_WithRecentSlipAndAbstinenceYesterday_NeverPromptsStartingOrResuming(int hour)
    {
        using var h = new Harness(hour);
        var habit = Habit.Create(new HabitCreateParams(h.User.Id, "Smoke", FrequencyUnit.Day, 1,
            Today.AddDays(-20), IsBadHabit: true,
            CreatedAtUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))).Value;
        var slip = habit.Log(Today.AddDays(-2), advanceDueDate: false);
        slip.IsSuccess.Should().BeTrue();
        slip.Value.IsSlip.Should().BeTrue();
        h.Db.Habits.Add(habit);

        await h.Run();

        h.Db.ProactiveItems.Should().BeEmpty();
        (await h.Engine.GetCurrentAsync(h.User.Id, default)).Should().BeNull();
        await h.Push.DidNotReceiveWithAnyArgs().SendToUserAsync(default, default!, default!, default!, default);
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(1, true, true)]
    public async Task RestartAfterMiss_OnlyCountsSuccessfulActivity(int slipDaysAgo, bool positiveActivity, bool restartExpected)
    {
        using var h = new Harness(8);
        var good = HabitFor(h.User.Id);
        if (positiveActivity) good.Log(Today.AddDays(-3), advanceDueDate: false).IsSuccess.Should().BeTrue();
        var bad = Habit.Create(new HabitCreateParams(h.User.Id, "Smoke", FrequencyUnit.Day, 1,
            Today.AddDays(-20), IsBadHabit: true,
            CreatedAtUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))).Value;
        bad.Log(Today.AddDays(-slipDaysAgo), advanceDueDate: false).IsSuccess.Should().BeTrue();
        h.Db.Habits.AddRange(good, bad);

        await h.Run();

        h.Db.ProactiveItems.Any(i => i.Trigger == ProactiveTrigger.RestartAfterMiss).Should().Be(restartExpected);
        var morning = await h.Db.ProactiveItems.SingleAsync(i => i.Trigger == ProactiveTrigger.MorningPlan);
        JsonSerializer.Deserialize<ProactiveFacts>(morning.FactsJson)!.HabitTitles.Should().Equal(good.Title);
    }

    [Theory]
    [InlineData(8, 0, false)]
    [InlineData(19, 0, false)]
    [InlineData(8, 1, true)]
    [InlineData(19, 1, true)]
    public async Task FlexibleQuota_UsesTheOwnersWeekStartBeforePrompting(int hour, int weekStartDay, bool expected)
    {
        using var h = new Harness(hour);
        h.User.SetWeekStartDay(weekStartDay);
        var habit = Habit.Create(new HabitCreateParams(h.User.Id, "Read", FrequencyUnit.Week, 1,
            Today.AddDays(-20), IsFlexible: true,
            CreatedAtUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))).Value;
        habit.Log(Today.AddDays(-1), advanceDueDate: false, weekStartDay: weekStartDay).IsSuccess.Should().BeTrue();
        h.Db.Habits.Add(habit);

        await h.Run();

        var trigger = hour == 8 ? ProactiveTrigger.MorningPlan : ProactiveTrigger.EveningCheckin;
        h.Db.ProactiveItems.Any(i => i.Trigger == trigger).Should().Be(expected);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(19)]
    public async Task Current_ResolvesFlexibleItemWhenQuotaIsMetOnAnotherDay(int hour)
    {
        using var h = new Harness(hour);
        var habit = Habit.Create(new HabitCreateParams(h.User.Id, "Read", FrequencyUnit.Week, 1,
            Today.AddDays(-20), IsFlexible: true,
            CreatedAtUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))).Value;
        h.Db.Habits.Add(habit);
        await h.Run();
        var current = await h.Engine.GetCurrentAsync(h.User.Id, default);
        current.Should().NotBeNull();
        current!.Trigger.Should().Be(hour == 8 ? ProactiveTrigger.MorningPlan : ProactiveTrigger.EveningCheckin);
        habit = await h.Db.Habits.SingleAsync();
        var log = habit.Log(Today.AddDays(-1), advanceDueDate: false, weekStartDay: 0);
        log.IsSuccess.Should().BeTrue();
        h.Db.HabitLogs.Add(log.Value);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.Engine.GetCurrentAsync(h.User.Id, default)).Should().BeNull();

        var ended = await h.Db.ProactiveItems.SingleAsync(i => i.Id == current.Id);
        ended.EndReason.Should().Be(ProactiveEndReason.ConditionResolved);
        ended.ActedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task CalendarFailure_IsSkippedAndNotRetriedThatMorning()
    {
        using var h = new Harness(8);
        h.SeedTrigger(ProactiveTrigger.BusyCalendarDay);
        h.Calendar.FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), null, Arg.Any<CancellationToken>())
            .Returns<List<CalendarEventItem>>(_ => throw new InvalidOperationException("Unavailable"));
        await h.Run();
        h.AtHour(9);
        await h.Run();
        h.Db.ProactiveItems.Any(i => i.Trigger == ProactiveTrigger.BusyCalendarDay).Should().BeFalse();
        await h.Calendar.Received(1).FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), null, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(22)]
    [InlineData(23)]
    public async Task QuietHours_NeverPush(int hour)
    {
        using var h = new Harness(hour);
        h.SeedTrigger(ProactiveTrigger.MorningPlan);
        await h.Run();
        h.Db.ProactiveItems.Any(i => i.PushedAtUtc.HasValue).Should().BeFalse();
        await h.Push.DidNotReceiveWithAnyArgs().SendToUserAsync(default, default!, default!, default!, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThreeIgnoredItems_RestForSevenDays_EngagementResetsTheCount(bool engaged)
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        for (var daysAgo = 3; daysAgo >= 1; daysAgo--)
        {
            var now = Today.AddDays(-daysAgo).ToDateTime(new TimeOnly(19, 0), DateTimeKind.Utc);
            var item = Item(h.User.Id, ProactiveTrigger.EveningCheckin, DateOnly.FromDateTime(now), now);
            item.Show(now);
            if (engaged && daysAgo == 2) item.Open(now.AddMinutes(1));
            item.End(ProactiveEndReason.WindowClosed, now.AddHours(3));
            h.Db.ProactiveItems.Add(item);
        }
        await h.Run();
        h.Db.ProactiveItems.Any(i => i.LocalDate == Today).Should().Be(engaged);
        if (!engaged)
        {
            h.Clock.Now = Today.AddDays(6).ToDateTime(new TimeOnly(21, 0), DateTimeKind.Utc);
            await h.Run();
            h.Db.ProactiveItems.Any(i => i.LocalDate == Today.AddDays(6)).Should().BeFalse();
            h.Clock.Now = Today.AddDays(7).ToDateTime(new TimeOnly(19, 0), DateTimeKind.Utc);
            await h.Run();
            h.Db.ProactiveItems.Any(i => i.LocalDate == Today.AddDays(7)).Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StoredSetting_ControlsPushButNotItems(bool? choice, bool pushes)
    {
        using var h = new Harness(19);
        if (choice.HasValue) h.User.SetProactiveAstraEnabled(choice.Value);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        h.Db.ProactiveItems.Count().Should().Be(1);
        h.Db.ProactiveItems.Single().PushedAtUtc.HasValue.Should().Be(pushes);
        h.Db.Users.Single().ProactiveAstraEnabled.Should().Be(choice);
    }

    [Fact]
    public async Task TrialIsEligible_FreeAndExpiredTrialAreNot()
    {
        using var h = new Harness(19, lifetime: false);
        h.User.StartTrial(h.Clock.Now.AddDays(1));
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        var free = User.Create("Free", "free@example.com").Value;
        free.StartTrial(h.Clock.Now.AddDays(-1));
        h.Db.Users.Add(free);
        h.Db.Habits.Add(HabitFor(free.Id));
        await h.Run();
        h.Db.ProactiveItems.Select(i => i.UserId).Distinct().Should().Equal(h.User.Id);
    }

    [Fact]
    public async Task KillSwitch_DisablesItemsAndPushAndCurrent()
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        h.Db.AppFeatureFlags.Add(AppFeatureFlag.Create(FeatureFlagKeys.AstraProactiveEngineDisabled, true));
        await h.Run();
        h.Db.ProactiveItems.Should().BeEmpty();
        (await h.Engine.GetCurrentAsync(h.User.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Outcomes_AreOwnerOnlyAndIdempotent()
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        var item = h.Db.ProactiveItems.Single();
        await h.Engine.GetCurrentAsync(h.User.Id, default);
        h.Clock.Now = h.Clock.Now.AddMinutes(5);
        (await h.Engine.RecordOutcomeAsync(Guid.NewGuid(), item.Id, false, default)).ErrorCode.Should().Be(ErrorCodes.ProactiveItemNotFound);
        (await h.Engine.RecordOutcomeAsync(Guid.NewGuid(), item.Id, true, default)).ErrorCode.Should().Be(ErrorCodes.ProactiveItemNotFound);
        (await h.Engine.RecordOutcomeAsync(h.User.Id, item.Id, false, default)).IsSuccess.Should().BeTrue();
        var opened = item.OpenedAtUtc;
        h.AtHour(20);
        await h.Engine.RecordOutcomeAsync(h.User.Id, item.Id, false, default);
        item.OpenedAtUtc.Should().Be(opened);
        await h.Engine.RecordOutcomeAsync(h.User.Id, item.Id, true, default);
        var acted = item.ActedAtUtc;
        h.AtHour(21);
        await h.Engine.RecordOutcomeAsync(h.User.Id, item.Id, true, default);
        item.ActedAtUtc.Should().Be(acted);
        item.EndReason.Should().Be(ProactiveEndReason.Acted);
        h.Analytics.Received(1).CaptureUserEvent(h.User.Id, "astra_proactive_item_created", Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, object>?>(p => p != null && (bool)p["pushed"]));
        h.Analytics.Received(1).CaptureUserEvent(h.User.Id, "astra_proactive_item_ended", Arg.Any<string>(),
            Arg.Is<IReadOnlyDictionary<string, object>?>(p => p != null && (string)p["outcome"] == "acted"
                && (double)p["minutes_from_shown_to_opened"] == 5));
        (await h.Engine.GetCurrentAsync(h.User.Id, default)).Should().BeNull();
    }

    [Theory]
    [InlineData(22, ProactiveEndReason.WindowClosed)]
    [InlineData(43, ProactiveEndReason.DayEnded)]
    public async Task Items_EndWhenWindowOrDayCloses(int hours, ProactiveEndReason reason)
    {
        using var h = new Harness(19);
        h.SeedTrigger(ProactiveTrigger.EveningCheckin);
        await h.Run();
        h.Clock.Now = Today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(hours);
        (await h.Engine.GetCurrentAsync(h.User.Id, default)).Should().BeNull();
        h.Db.ProactiveItems.Single().EndReason.Should().Be(reason);
    }

    [Fact]
    public async Task GoalCooldown_IsPerGoalAndLocalDate()
    {
        using var h = new Harness(19);
        var goal = h.SeedGoal();
        var now = h.Clock.Now.AddDays(-2);
        var old = Item(h.User.Id, ProactiveTrigger.GoalBehind, Today.AddDays(-2), now, goal.Id);
        old.Act(now);
        h.Db.ProactiveItems.Add(old);
        await h.Run();
        h.Db.ProactiveItems.Count().Should().Be(1);
        h.Clock.Now = h.Clock.Now.AddDays(1);
        await h.Run();
        h.Db.ProactiveItems.Count().Should().Be(2);
    }

    [Theory]
    [InlineData("America/New_York", "2030-11-03T12:00:00Z", false)]
    [InlineData("America/New_York", "2030-11-03T13:00:00Z", true)]
    [InlineData("America/New_York", "2030-03-10T11:00:00Z", false)]
    [InlineData("America/New_York", "2030-03-10T12:00:00Z", true)]
    [InlineData("invalid/zone", "2030-10-14T07:00:00Z", false)]
    [InlineData("invalid/zone", "2030-10-14T08:00:00Z", true)]
    public async Task WindowsAndQuietHours_UseLocalClockAcrossDstAndInvalidZones(string zone, string utc, bool pushExpected)
    {
        using var h = new Harness(8);
        h.User.SetTimeZone(zone);
        h.Clock.Now = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(h.Clock.Now, TimeZoneHelper.FindTimeZone(zone)));
        h.SeedTrigger(ProactiveTrigger.BusyCalendarDay);
        h.Calendar.FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), null, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, 4).Select(i => Event(14 + i, 15 + i, day)).ToList());
        await h.Run();
        h.Db.ProactiveItems.Any(i => i.PushedAtUtc.HasValue).Should().Be(pushExpected);
    }

    [Fact]
    public async Task HourlyRun_UsesSameReadCountForTenAndThousandEligibleAccounts()
    {
        var small = await CountReads(10);
        var large = await CountReads(1000);
        large.Should().Be(small);
        small.Should().BeGreaterThan(0);
    }

    private static async Task<int> CountReads(int count)
    {
        var interceptor = new CountingDbCommandInterceptor();
        using var h = new Harness(19, interceptor);
        h.Db.Users.Remove(h.User);
        for (var i = 0; i < count; i++)
        {
            var user = User.Create("Person", $"person{i}@example.com").Value;
            user.GrantLifetimePro();
            user.SetWeekStartDay(0);
            h.Db.Users.Add(user);
            h.Db.Habits.Add(HabitFor(user.Id));
        }
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        interceptor.Reset();
        await h.Engine.EvaluateAsync(default);
        var queries = interceptor.SelectCommandCount;
        h.Db.ProactiveItems.Count().Should().Be(count);
        return queries;
    }

    internal static ProactiveItem Item(Guid userId, ProactiveTrigger trigger, DateOnly date, DateTime now, Guid? goalId = null) =>
        ProactiveItem.Create(userId, trigger, date, "{}", "Text.", "Action", "Prompt", now, goalId);

    private static Habit HabitFor(Guid userId) => Habit.Create(new HabitCreateParams(userId, "Read", FrequencyUnit.Day, 1,
        Today.AddDays(-20), CreatedAtUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))).Value;

    private static CalendarEventItem Event(int start, int end, DateOnly? day = null) => new(
        $"event-{start}", "Meeting", null, (day ?? Today).ToString("yyyy-MM-dd"), $"{start}:00", $"{end}:00", false, null, [],
        (day ?? Today).ToDateTime(new TimeOnly(start, 0), DateTimeKind.Utc),
        EndUtc: (day ?? Today).ToDateTime(new TimeOnly(end, 0), DateTimeKind.Utc));

    internal sealed class FixedClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    internal sealed class Harness : IDisposable
    {
        private readonly SqliteOrbitDbContextFactory _factory;
        public OrbitDbContext Db => _factory.Context;
        public User User { get; } = User.Create("Alex", "alex@example.com").Value;
        public FixedClock Clock { get; }
        public ICalendarEventFetcher Calendar { get; } = Substitute.For<ICalendarEventFetcher>();
        public IPushNotificationService Push { get; } = Substitute.For<IPushNotificationService>();
        public IProductAnalytics Analytics { get; } = Substitute.For<IProductAnalytics>();
        public ProactiveEngineService Engine { get; }

        public Harness(int hour, CountingDbCommandInterceptor? interceptor = null, bool lifetime = true)
        {
            _factory = interceptor is null ? new() : new(interceptor);
            Clock = new(Today.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc));
            if (lifetime) User.GrantLifetimePro();
            User.SetTimeZone("UTC");
            User.SetWeekStartDay(0);
            Db.Users.Add(User);
            var messages = Substitute.For<IProactiveCheckinMessageService>();
            messages.GenerateAsync(Arg.Any<ProactiveFacts>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => AiProactiveCheckinMessageService.Template(call.Arg<ProactiveFacts>(), call.Arg<string>()));
            var tokens = Substitute.For<IGoogleTokenService>();
            tokens.GetValidAccessTokenAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns("access");
            tokens.TryRefreshAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(new GoogleTokenRefreshOutcome("access", GoogleTokenRefreshResult.Success, null));
            var dates = new UserDateService(Substitute.For<IGenericRepository<User>>(),
                new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), Clock);
            Engine = new(Db, Clock, messages, Push, Calendar, tokens, Analytics, dates,
                NullLogger<ProactiveEngineService>.Instance);
        }

        public void AtHour(int hour) => Clock.Now = Today.ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);
        public async Task Run()
        {
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            await Engine.EvaluateAsync(default);
        }

        public Goal SeedGoal()
        {
            var goal = Goal.Create(new Goal.CreateGoalParams(User.Id, "Learn", 10, "lessons", Deadline: Today.AddDays(-1))).Value;
            Db.Goals.Add(goal);
            return goal;
        }

        public void SeedTrigger(ProactiveTrigger trigger)
        {
            switch (trigger)
            {
                case ProactiveTrigger.GoalBehind: SeedGoal(); break;
                case ProactiveTrigger.BusyCalendarDay:
                    User.SetGoogleTokens("access", "refresh");
                    Calendar.FetchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>?>(), null, Arg.Any<CancellationToken>())
                        .Returns(Enumerable.Range(0, 4).Select(i => Event(9 + i, 10 + i)).ToList());
                    break;
                default:
                    if (trigger == ProactiveTrigger.WeeklyReview) User.SetWeekStartDay(1);
                    var habit = HabitFor(User.Id);
                    if (trigger == ProactiveTrigger.RestartAfterMiss)
                        habit.Log(Today.AddDays(-2), advanceDueDate: false);
                    Db.Habits.Add(habit);
                    break;
            }
        }

        public void Dispose() => _factory.Dispose();
    }
}
