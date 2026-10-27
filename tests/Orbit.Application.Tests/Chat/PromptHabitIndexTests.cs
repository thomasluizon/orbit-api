using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat;

public class PromptHabitIndexTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 8, 14);

    [Fact]
    public void BuildPromptHabitIndex_OverLimit_PrioritizesDueHabitsAndKeepsAncestors()
    {
        var completedParent = CreateHabit("Completed parent", Today, null, isOneTime: true);
        completedParent.Log(Today);
        var overdue = Enumerable.Range(0, 40)
            .Select(index => CreateHabit(
                $"Overdue {index}",
                Today.AddDays(-1),
                index == 0 ? completedParent.Id : null,
                isOneTime: true))
            .ToList();
        var dueToday = Enumerable.Range(0, 40)
            .Select(index => CreateHabit($"Today {index}", Today))
            .ToList();
        var future = Enumerable.Range(0, 920)
            .Select(index => CreateHabit($"Future {index}", Today.AddDays(1)))
            .ToList();
        var habits = new[] { completedParent }
            .Concat(overdue)
            .Concat(dueToday)
            .Concat(future)
            .ToList();

        var result = ProcessUserChatCommandHandler.BuildPromptHabitIndex(habits, Today);
        var selectedIds = result.Habits.Select(habit => habit.Id).ToHashSet();

        result.IsPartial.Should().BeTrue();
        result.Habits.Should().HaveCount(AppConstants.MaxPromptHabitEntries);
        overdue.Should().OnlyContain(habit => selectedIds.Contains(habit.Id));
        dueToday.Should().OnlyContain(habit => selectedIds.Contains(habit.Id));
        foreach (var selectedHabit in result.Habits.Where(habit => habit.ParentHabitId.HasValue))
            selectedIds.Should().Contain(selectedHabit.ParentHabitId!.Value);
    }

    [Fact]
    public void BuildPromptHabitIndex_LoggedToday_CarriesDoneTodayFact()
    {
        var habit = CreateHabit("Water", Today);
        habit.Log(Today).IsSuccess.Should().BeTrue();

        var result = ProcessUserChatCommandHandler.BuildPromptHabitIndex([habit], Today);

        result.Habits.Should().ContainSingle().Which.NextDueDate.Should().Be(Today.AddDays(1));
        result.DoneTodayHabitIds.Should().Contain(habit.Id);
    }

    [Fact]
    public void BuildPromptHabitIndex_BadHabitSlipToday_DoesNotCarryDoneTodayFact()
    {
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, "Smoking", FrequencyUnit.Day, 1, DueDate: Today, IsBadHabit: true)).Value;
        habit.Log(Today).Value.IsSlip.Should().BeTrue();

        var result = ProcessUserChatCommandHandler.BuildPromptHabitIndex([habit], Today);

        result.DoneTodayHabitIds.Should().NotContain(habit.Id);
    }

    [Fact]
    public async Task SelectPromptHabitIndex_RechecksDemotedCandidatesAndMatchesCompleteHistory()
    {
        var flexible = Enumerable.Range(0, AppConstants.MaxPromptHabitEntries + 1).Select(index =>
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Flexible, UserId,
                $"Flexible {index}", FrequencyUnit.Week, 2, Today.AddDays(-30), Position: index)).Value;
            habit.Log(Today.AddDays(-1), advanceDueDate: false);
            habit.Log(Today.AddDays(-2), advanceDueDate: false);
            return habit;
        }).ToArray();
        var daily = Enumerable.Range(0, AppConstants.MaxPromptHabitEntries)
            .Select(index =>
            {
                var habit = CreateHabit($"Daily {index}", Today);
                habit.SetPosition(index + AppConstants.MaxPromptHabitEntries + 1);
                return habit;
            }).ToArray();
        var habits = flexible.Concat(daily).ToArray();
        var reader = ReaderFor(habits);
        var completeFacts = await HabitTodaySnapshot.ReadAsync(habits, Today, 1, reader, default);
        var expected = ProcessUserChatCommandHandler.BuildPromptHabitIndex(habits, Today, completeFacts);
        reader.ClearReceivedCalls();

        var (actual, facts) = await ProcessUserChatCommandHandler.SelectPromptHabitIndexAsync(
            habits, Today, 1, new HashSet<Guid>(), reader, default);

        actual.Habits.Select(habit => habit.Id).Should().Equal(expected.Habits.Select(habit => habit.Id));
        actual.Habits.Should().OnlyContain(habit => daily.Contains(habit));
        facts.TodayIds.Intersect(actual.Habits.Select(habit => habit.Id))
            .Should().BeEquivalentTo(completeFacts.TodayIds.Intersect(actual.Habits.Select(habit => habit.Id)));
        var batches = reader.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(reader.ReadDaysAsync))
            .Select(call => (IReadOnlyCollection<Guid>)call.GetArguments()[0]!).ToArray();
        batches.Should().HaveCount(2).And.OnlyContain(ids => ids.Count <= AppConstants.MaxPromptHabitEntries);
        batches.SelectMany(ids => ids).Should().BeEquivalentTo(flexible.Select(habit => habit.Id));
    }

    [Fact]
    public async Task SelectPromptHabitIndex_PreservesCompletedAncestorsAndDoneTodayWithoutHistory()
    {
        var parent = CreateHabit("Completed parent", Today, isOneTime: true);
        parent.Log(Today);
        var child = CreateHabit("Child", Today, parent.Id);
        var future = CreateHabit("Future", Today.AddDays(1));
        var reader = ReaderFor([parent, child, future]);

        var (index, facts) = await ProcessUserChatCommandHandler.SelectPromptHabitIndexAsync(
            [parent, child, future], Today, 1, new HashSet<Guid> { parent.Id }, reader, default);

        index.Habits.Should().Equal(parent, child, future);
        facts.DoneTodayIds.Should().Contain(parent.Id);
        facts.TodayIds.Should().Contain(parent.Id).And.Contain(child.Id).And.NotContain(future.Id);
        reader.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SelectPromptHabitIndex_ResolvedPastOccurrencesDoNotDisplaceTodaysHabits(int weekStartDay)
    {
        var resolved = Enumerable.Range(0, AppConstants.MaxPromptHabitEntries).Select(index =>
        {
            var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, UserId,
                $"Weekly {index}", FrequencyUnit.Week, 1, Today.AddDays(-1), Position: index)).Value;
            habit.Log(Today.AddDays(-1), advanceDueDate: false);
            return habit;
        }).ToArray();
        var daily = Enumerable.Range(0, AppConstants.MaxPromptHabitEntries).Select(index =>
        {
            var habit = CreateHabit($"Daily {index}", Today);
            habit.SetPosition(index + AppConstants.MaxPromptHabitEntries);
            return habit;
        }).ToArray();
        var habits = resolved.Concat(daily).ToArray();
        var reader = ReaderFor(habits);
        var fullFacts = await HabitTodaySnapshot.ReadAsync(habits, Today, weekStartDay, reader, default);
        var expected = ProcessUserChatCommandHandler.BuildPromptHabitIndex(habits, Today, fullFacts);
        var candidates = ChatHabitCandidate.Select(habits.AsQueryable(), Today, false)
            .AsEnumerable().Select(candidate => candidate.ToHabit(UserId)).ToArray();

        var (actual, facts) = await ProcessUserChatCommandHandler.SelectPromptHabitIndexAsync(
            candidates, Today, weekStartDay, new HashSet<Guid>(), reader, default);

        actual.Habits.Select(habit => habit.Id).Should().Equal(expected.Habits.Select(habit => habit.Id));
        actual.Habits.Select(habit => habit.Id).Should().BeEquivalentTo(daily.Select(habit => habit.Id));
        facts.OverdueIds.Intersect(actual.Habits.Select(habit => habit.Id)).Should().BeEmpty();
    }

    private static IHabitScheduleLogReader ReaderFor(IReadOnlyList<Habit> habits)
    {
        var reader = Substitute.For<IHabitScheduleLogReader>();
        reader.ReadDaysAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>()).Returns(call =>
        {
            var ids = call.Arg<IReadOnlyCollection<Guid>>();
            var from = call.ArgAt<DateOnly>(1);
            var to = call.ArgAt<DateOnly>(2);
            return habits.Where(habit => ids.Contains(habit.Id)).SelectMany(habit => habit.Logs
                .Where(log => !log.IsDeleted && log.Date >= from && log.Date <= to)
                .GroupBy(log => log.Date).Select(group => new HabitScheduleLogDay(habit.Id, group.Key,
                    group.Count(log => log.Value > 0), group.Count(log => log.Value == 0), true))).ToArray();
        });
        return reader;
    }

    private static Habit CreateHabit(
        string title,
        DateOnly dueDate,
        Guid? parentHabitId = null,
        bool isOneTime = false)
    {
        var habit = Habit.Create(new HabitCreateParams(((FrequencyUnit?)(isOneTime ? null : (FrequencyUnit?)FrequencyUnit.Day)) is null ? HabitSchedule.OneTime : HabitSchedule.Recurring,
            UserId,
            title,
            isOneTime ? null : FrequencyUnit.Day,
            isOneTime ? null : 1,
            DueDate: dueDate,
            ParentHabitId: parentHabitId)).Value;
        if (int.TryParse(title.Split(' ').LastOrDefault(), out var position))
            habit.SetPosition(position);
        return habit;
    }
}
