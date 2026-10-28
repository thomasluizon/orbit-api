using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public partial class HabitSuggestionTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private readonly DateOnly _today = new(2026, 10, 26);
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Goal> _goals = Substitute.For<IGenericRepository<Goal>>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IHabitDraftGenerator _generator = Substitute.For<IHabitDraftGenerator>();

    private HabitSuggestionService Service(params Habit[] habits)
    {
        _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(_today);
        _dates.GetUserWeekStartDayAsync(_userId, Arg.Any<CancellationToken>()).Returns(1);
        _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToArray());
        return new(_habits, _goals, _dates, _generator);
    }

    private HabitSuggestionDraft Draft(string title = "Read") => new(Guid.NewGuid(), "📖", title, "Habit", FrequencyUnit.Day,
        1, [], _today, new(22, 0), new(22, 15), null, [], "A quiet pause before bed.");

    private void Generate(params HabitSuggestionDraft[] drafts) => _generator
        .GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>())
        .Returns(Result.Success<IReadOnlyList<HabitSuggestionDraft>>(drafts));

    [Fact]
    public async Task Generate_LoadsOwnedActiveHabitsAndGoalsUsingLocalToday()
    {
        var own = Draft().ToHabit(_userId).Value;
        var other = Draft("Walk").ToHabit(Guid.NewGuid()).Value;
        var goal = Goal.Create(_userId, "Read more", 10, "books").Value;
        _goals.FindAsync(Arg.Any<Expression<Func<Goal, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => new[] { goal }.Where(call.Arg<Expression<Func<Goal, bool>>>().Compile()).ToArray());
        Generate(Draft("Stretch") with { DueTime = new(21, 0), DueEndTime = new(21, 10) });

        var result = await Service(own, other).GenerateAsync(_userId, "Suggest a sleep habit", "en", 1, null, default);

        result.IsSuccess.Should().BeTrue();
        await _generator.Received(1).GenerateAsync(Arg.Is<HabitSuggestionRequest>(request =>
            request.Today == _today && request.Habits.Count == 1 && request.Habits[0].Id == own.Id
            && request.Goals.Count == 1 && request.Goals[0].Id == goal.Id), Arg.Any<CancellationToken>());
        await _habits.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Meditação", "meditacao")]
    [InlineData("Read", "READ")]
    public async Task Duplicate_BecomesAnAdjustmentDraft(string existingTitle, string suggestedTitle)
    {
        var existing = Draft(existingTitle).ToHabit(_userId).Value;
        Generate(Draft(suggestedTitle));
        var result = await Service(existing).GenerateAsync(_userId, "ideas", "pt-BR", 1, null, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Drafts[0].ExistingHabitId.Should().Be(existing.Id);
        result.Value.Drafts[0].Title.Should().Be(existing.Title);
    }

    [Fact]
    public async Task Refine_ReturnsOnlyTheRequestedDraftWithStableIdentity()
    {
        var original = Draft();
        var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        Generate(original with { Id = Guid.NewGuid(), DueTime = new(22, 30), DueEndTime = new(22, 40), Days = weekdays });
        var payGate = Substitute.For<IPayGateService>();
        payGate.TryConsumeAiMessage(_userId, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ModerationResult(false, false, []));
        var handler = new RefineHabitSuggestionQueryHandler(Service(), payGate, Substitute.For<IUnitOfWork>(), moderation);
        var result = await handler.Handle(new(_userId, original, "weekdays at 22:30", "en"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(original.Id);
        result.Value.Title.Should().Be(original.Title);
        result.Value.DueTime.Should().Be(new TimeOnly(22, 30));
        result.Value.Days.Should().Equal(weekdays);
        await moderation.Received(1).CheckTextAsync(Arg.Is<string>(input => input.Contains(original.Title)
            && input.Contains("weekdays at 22:30")), Arg.Any<CancellationToken>());
        await _generator.Received(1).GenerateAsync(Arg.Is<HabitSuggestionRequest>(request =>
            request.Count == 1 && request.Original == original && request.Instruction == "weekdays at 22:30"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task Refine_PreservesTheModelScheduleAndStableIdentity(string language, bool fixedDays)
    {
        var original = Draft() with { FrequencyUnit = FrequencyUnit.Week, FrequencyQuantity = 3 };
        var generated = original with
        {
            Id = Guid.NewGuid(),
            FrequencyUnit = fixedDays ? FrequencyUnit.Day : FrequencyUnit.Week,
            FrequencyQuantity = fixedDays ? 1 : 5,
            Days = fixedDays ? [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday] : [],
            DueTime = new(22, 30),
            DueEndTime = new(22, 50)
        };
        Generate(generated);

        var instruction = language == "en" ? "weekdays at 22:30" : "só nos dias de semana, às 22h30";
        var result = await Service().GenerateAsync(_userId, instruction, language, 1, original, default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var draft = result.Value.Drafts.Should().ContainSingle().Which;
        draft.Should().BeEquivalentTo(generated with { Id = original.Id });
        await _generator.Received(1).GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task Overlap_IsRepairedBeforeReturningADraft(string language)
    {
        var existing = Draft("Work").ToHabit(_userId).Value;
        var repair = Draft() with { DueTime = new(22, 15), DueEndTime = new(22, 30) };
        _generator.GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>()).Returns(
            Result.Success<IReadOnlyList<HabitSuggestionDraft>>([Draft()]),
            Result.Success<IReadOnlyList<HabitSuggestionDraft>>([repair]));
        var result = await Service(existing).GenerateAsync(_userId, "ideas", language, 1, null, default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Drafts.Should().Equal(repair);
        await _generator.Received(1).GenerateAsync(Arg.Is<HabitSuggestionRequest>(request => request.Correction != null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepeatedOverlap_IsRejectedWithoutWriting()
    {
        var existing = Draft("Work").ToHabit(_userId).Value;
        Generate(Draft());
        var result = await Service(existing).GenerateAsync(_userId, "ideias", "pt-BR", 1, null, default);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(HabitSuggestionService.Unavailable("pt-BR"));
        await _generator.Received(2).GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Normalize_RejectsDuplicatesWithinTheSetAndForeignTargets()
    {
        var request = new HabitSuggestionRequest(_userId, "ideas", "en", 2, _today, [], []);
        HabitSuggestionService.Normalize([Draft(), Draft("READ")], request).IsFailure.Should().BeTrue();
        HabitSuggestionService.Normalize([Draft() with { ExistingHabitId = Guid.NewGuid() }], request with { Count = 1 })
            .IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_AlwaysRequestsExistingApprovalPath(bool duplicate)
    {
        var draft = Draft();
        var existing = draft.ToHabit(_userId).Value;
        var executor = Substitute.For<IAgentOperationExecutor>();
        var handler = new PreviewHabitSuggestionCommandHandler(Service(duplicate ? [existing] : []), executor);
        var result = await handler.Handle(new(_userId, draft), default);
        result.IsSuccess.Should().BeTrue();
        await executor.Received(1).ExecuteAsync(Arg.Is<AgentExecuteOperationRequest>(request =>
            request.PreviewOnly && request.IncludeChangePreview && request.UserId == _userId
            && request.OperationId == (duplicate ? "update_habit" : "create_habit")), Arg.Any<CancellationToken>());
        await _habits.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HabitSchedule.Recurring, 2)]
    [InlineData(HabitSchedule.Flexible, 1)]
    public async Task Preview_AdjustmentPreservesCadenceUnlessTheDraftChangesIt(HabitSchedule schedule, int intervalWeeks)
    {
        var existing = Habit.Create(new HabitCreateParams(schedule, _userId, "Read", FrequencyUnit.Day, 1,
            _today, DueTime: new(22, 0), DueEndTime: new(22, 15), IntervalWeeks: intervalWeeks)).Value;
        var draft = Draft() with { Days = schedule == HabitSchedule.Flexible ? [DayOfWeek.Monday] : [] };
        var executor = Substitute.For<IAgentOperationExecutor>();
        var handler = new PreviewHabitSuggestionCommandHandler(Service(existing), executor);

        var preview = await handler.Handle(new(_userId, draft), default);

        preview.IsSuccess.Should().BeTrue(preview.Error);
        var call = executor.ReceivedCalls().Single();
        var arguments = ((AgentExecuteOperationRequest)call.GetArguments()[0]!).Arguments;
        var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(arguments, allowHabitId: true);
        error.Should().BeNull();
        var resolved = HabitUpdateValidation.ResolveSingleUpdate(existing, changes!, _today);
        resolved.IsSuccess.Should().BeTrue(resolved.Error);
        existing.Update(resolved.Value).IsSuccess.Should().BeTrue();

        existing.Schedule.Should().Be(HabitSchedule.Recurring);
        if (schedule == HabitSchedule.Recurring)
        {
            existing.IntervalWeeks.Should().Be(intervalWeeks);
            HabitScheduleService.IsHabitDueOnDate(existing, _today.AddDays(7)).Should().BeFalse();
        }
        else
            HabitScheduleService.GetScheduledDates(existing, _today, _today.AddDays(366)).Should()
                .Equal(HabitScheduleService.GetScheduledDates(draft.ToHabit(_userId).Value, _today, _today.AddDays(366)));
    }

    [Fact]
    public async Task Preview_UnappliableAdjustmentReturnsTypedErrorWithoutDispatch()
    {
        var parent = Habit.Create(new HabitCreateParams(HabitSchedule.General, _userId, "Reading", null, null, _today)).Value;
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.General, _userId, "Read", null, null,
            _today, ParentHabitId: parent.Id)).Value;
        var executor = Substitute.For<IAgentOperationExecutor>();
        var handler = new PreviewHabitSuggestionCommandHandler(Service(parent, existing), executor);

        var result = await handler.Handle(new(_userId, Draft()), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("HABIT_SUGGESTION_UNAVAILABLE");
        executor.ReceivedCalls().Should().BeEmpty();
        existing.Schedule.Should().Be(HabitSchedule.General);
    }

    [Theory]
    [InlineData("days")]
    [InlineData("checklist")]
    [InlineData("reminder")]
    public async Task Preview_AdjustmentHonorsExplicitOptionalClears(string field)
    {
        var original = Draft() with
        {
            Days = [DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday],
            ChecklistItems = ["Choose a book"],
            ReminderMinutesBefore = 30
        };
        var existing = original.ToHabit(_userId).Value;
        var draft = original with { ExistingHabitId = existing.Id };
        draft = field switch
        {
            "days" => draft with { Days = [] },
            "checklist" => draft with { ChecklistItems = [] },
            _ => draft with { ReminderMinutesBefore = null }
        };
        var executor = Substitute.For<IAgentOperationExecutor>();

        var result = await new PreviewHabitSuggestionCommandHandler(Service(existing), executor)
            .Handle(new(_userId, draft), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        var arguments = ((AgentExecuteOperationRequest)executor.ReceivedCalls().Single().GetArguments()[0]!).Arguments;
        var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(arguments, allowHabitId: true);
        error.Should().BeNull();
        var update = HabitUpdateValidation.ResolveSingleUpdate(existing, changes!, _today);
        update.IsSuccess.Should().BeTrue(update.Error);
        existing.Update(update.Value).IsSuccess.Should().BeTrue();
        existing.Days.Should().Equal(field == "days" ? [] : original.Days);
        existing.ChecklistItems.Select(item => item.Text).Should().Equal(field == "checklist" ? [] : original.ChecklistItems);
        existing.ReminderEnabled.Should().Be(field != "reminder");
        existing.ReminderTimes.Should().Equal(field == "reminder" ? [] : new[] { 30 });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(FrequencyUnit.Day)]
    [InlineData(FrequencyUnit.Week)]
    [InlineData(FrequencyUnit.Month)]
    [InlineData(FrequencyUnit.Year)]
    public async Task Preview_AdjustmentReplacesInheritedAnchorAndCutoff(FrequencyUnit? unit)
    {
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Read", FrequencyUnit.Day, 3,
            _today.AddDays(-30), EndDate: _today.AddDays(4), IntervalWeeks: 2)).Value;
        var draft = Draft() with
        {
            Type = unit.HasValue ? "Habit" : "Task",
            FrequencyUnit = unit,
            FrequencyQuantity = unit.HasValue ? 1 : null,
            DueDate = _today.AddDays(5)
        };
        var executor = Substitute.For<IAgentOperationExecutor>();
        var handler = new PreviewHabitSuggestionCommandHandler(Service(existing), executor);

        var result = await handler.Handle(new(_userId, draft), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        existing.EndDate.Should().Be(_today.AddDays(4));
        var arguments = ((AgentExecuteOperationRequest)executor.ReceivedCalls().Single().GetArguments()[0]!).Arguments;
        var (changes, error) = BulkHabitToolArguments.ParseUpdateFields(arguments, allowHabitId: true);
        error.Should().BeNull();
        var update = HabitUpdateValidation.ResolveSingleUpdate(existing, changes!, _today);
        update.IsSuccess.Should().BeTrue(update.Error);
        existing.Update(update.Value).IsSuccess.Should().BeTrue();
        existing.EndDate.Should().BeNull();
        existing.NextDueDate.Should().Be(draft.DueDate);
        if (unit.HasValue) existing.StartDate.Should().Be(draft.DueDate);
        HabitScheduleService.GetScheduledDates(existing, _today, _today.AddDays(366)).Should()
            .Equal(HabitScheduleService.GetScheduledDates(draft.ToHabit(_userId).Value, _today, _today.AddDays(366)));
    }

    [Fact]
    public async Task Preview_ConflictsUsePreservedAnchorAndEffectiveDueDate()
    {
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Read", FrequencyUnit.Day, 1,
            _today.AddDays(-1))).Value;
        existing.Log(_today, advanceDueDate: false).IsSuccess.Should().BeTrue();
        var busy = (Draft("Appointment") with { Type = "Task", FrequencyUnit = null, FrequencyQuantity = null })
            .ToHabit(_userId).Value;
        var executor = Substitute.For<IAgentOperationExecutor>();
        var handler = new PreviewHabitSuggestionCommandHandler(Service(existing, busy), executor);

        var result = await handler.Handle(new(_userId, Draft()), default);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("HABIT_SUGGESTION_UNAVAILABLE");
        executor.ReceivedCalls().Should().BeEmpty();
        existing.NextDueDate.Should().Be(_today.AddDays(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Validators_RejectInvalidCountsAndIncompleteDrafts(int count)
    {
        new SuggestHabitDraftsQueryValidator().Validate(new SuggestHabitDraftsQuery(_userId, "ideas", "en", count)).IsValid.Should().BeFalse();
        new RefineHabitSuggestionQueryValidator().Validate(new RefineHabitSuggestionQuery(_userId, Draft() with { Emoji = "" }, "easier")).IsValid.Should().BeFalse();
        new PreviewHabitSuggestionCommandValidator().Validate(new PreviewHabitSuggestionCommand(_userId, Draft() with { DueEndTime = new(20, 0) })).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("moderated")]
    [InlineData("unavailable")]
    [InlineData("allowance")]
    public async Task Refinement_RespectsModerationAndMessageAllowance(string blockedBy)
    {
        var moderation = Substitute.For<IContentModerationService>();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ModerationResult(blockedBy == "moderated", blockedBy == "unavailable", []));
        var payGate = Substitute.For<IPayGateService>();
        payGate.TryConsumeAiMessage(_userId, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Message allowance used."));
        var handler = new RefineHabitSuggestionQueryHandler(Service(), payGate, Substitute.For<IUnitOfWork>(), moderation);

        var result = await handler.Handle(new(_userId, Draft(), "make it easier"), default);

        result.IsFailure.Should().BeTrue();
        await _generator.DidNotReceive().GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>());
        if (blockedBy != "allowance")
            await payGate.DidNotReceive().TryConsumeAiMessage(Arg.Any<Guid>(), Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void DraftSet_AcceptsCompleteDistinctDraftsAndRejectsOverlaps(int count)
    {
        var drafts = Enumerable.Range(0, count).Select(index => Draft($"Read {index}") with
        { DueTime = new(20 + index / 4, index % 4 * 15), DueEndTime = new(20 + index / 4, index % 4 * 15 + 10) }).ToArray();
        var request = new HabitSuggestionRequest(_userId, "ideas", "en", count, _today, [], []);
        HabitSuggestionService.Normalize(drafts, request).IsSuccess.Should().BeTrue();
        var overlapping = new[] { Draft("Read"), Draft("Stretch") };
        HabitSuggestionService.Normalize(overlapping, request with { Count = 2 }).IsFailure.Should().BeTrue();
    }
}
