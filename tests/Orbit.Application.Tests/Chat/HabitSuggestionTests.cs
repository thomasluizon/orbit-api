using System.Linq.Expressions;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public class HabitSuggestionTests
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
    }

    [Fact]
    public async Task Refine_ReturnsOnlyTheRequestedDraftWithStableIdentity()
    {
        var original = Draft();
        Generate(original with { Id = Guid.NewGuid(), DueTime = new(22, 30), DueEndTime = new(22, 40), Days = [DayOfWeek.Monday] });
        var handler = new RefineHabitSuggestionQueryHandler(Service());
        var result = await handler.Handle(new(_userId, original, "weekdays at 22:30", "en"), default);
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be(original.Id);
        result.Value.Title.Should().Be(original.Title);
        result.Value.DueTime.Should().Be(new TimeOnly(22, 30));
        await _generator.Received(1).GenerateAsync(Arg.Is<HabitSuggestionRequest>(request =>
            request.Count == 1 && request.Original == original && request.Instruction == "weekdays at 22:30"), Arg.Any<CancellationToken>());
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
    [InlineData(0)]
    [InlineData(6)]
    public void Validators_RejectInvalidCountsAndIncompleteDrafts(int count)
    {
        new SuggestHabitDraftsQueryValidator().Validate(new SuggestHabitDraftsQuery(_userId, "ideas", "en", count)).IsValid.Should().BeFalse();
        new RefineHabitSuggestionQueryValidator().Validate(new RefineHabitSuggestionQuery(_userId, Draft() with { Emoji = "" }, "easier")).IsValid.Should().BeFalse();
        new PreviewHabitSuggestionCommandValidator().Validate(new PreviewHabitSuggestionCommand(_userId, Draft() with { DueEndTime = new(20, 0) })).IsValid.Should().BeFalse();
    }
}
