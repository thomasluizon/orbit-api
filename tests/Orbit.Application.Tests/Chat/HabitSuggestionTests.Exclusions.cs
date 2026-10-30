using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public partial class HabitSuggestionTests
{
    [Theory]
    [InlineData("Stretch", "STRETCH")]
    [InlineData("Alongar", "Alongar pela manhã")]
    [InlineData("Alongar pela manhã", "Alongar")]
    [InlineData("Meditação", "MEDITACAO")]
    public async Task ExcludedActivity_IsCorrectedUsingStructuredTitles(string planned, string repeated)
    {
        var replacement = Draft();
        _generator.GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>()).Returns(
            Result.Success<IReadOnlyList<HabitSuggestionDraft>>([Draft(repeated)]),
            Result.Success<IReadOnlyList<HabitSuggestionDraft>>([replacement]));

        var result = await Service().GenerateAsync(_userId, "ideas", "pt-BR", 1, null, default, [planned]);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Drafts.Should().Equal(replacement);
        await _generator.Received(1).GenerateAsync(Arg.Is<HabitSuggestionRequest>(request => request.Correction != null
            && request.ExcludedActivities != null && request.ExcludedActivities.SequenceEqual(new[] { planned })), Arg.Any<CancellationToken>());
        await _habits.DidNotReceive().AddAsync(Arg.Any<Orbit.Domain.Entities.Habit>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Exclusions_RejectAccountActivitiesWithOrWithoutAnExistingId(bool identified, bool completed)
    {
        var existing = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Read",
            FrequencyUnit.Day, 1, _today, EndDate: _today)).Value;
        if (completed)
        {
            existing.Log(_today).IsSuccess.Should().BeTrue();
            existing.IsCompleted.Should().BeTrue();
        }
        Generate(Draft(identified ? "A renamed activity" : "READ") with
        { ExistingHabitId = identified ? existing.Id : null });

        var result = await Service(existing).GenerateAsync(_userId, "ideas", "en", 1, null, default, []);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("HABIT_SUGGESTION_UNAVAILABLE");
        await _generator.Received(2).GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepeatedExcludedActivity_ReturnsTypedFailureWithoutDrafts()
    {
        Generate(Draft("Stretch"));

        var result = await Service().GenerateAsync(_userId, "ideas", "en", 1, null, default, ["Stretch"]);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("HABIT_SUGGESTION_UNAVAILABLE");
        await _generator.Received(2).GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompletedAccountActivity_DoesNotReserveTimeForANewIdea()
    {
        var completed = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Stretch",
            FrequencyUnit.Day, 1, _today, DueTime: new(22, 0), DueEndTime: new(22, 15), EndDate: _today)).Value;
        completed.Log(_today).IsSuccess.Should().BeTrue();
        completed.IsCompleted.Should().BeTrue();
        Generate(Draft());

        var result = await Service(completed).GenerateAsync(_userId, "ideas", "en", 1, null, default, []);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Drafts.Should().ContainSingle().Which.Title.Should().Be("Read");
    }

    [Fact]
    public void SuggestTool_ExclusionArgumentIsOptionalInSchema()
    {
        var schema = JsonSerializer.SerializeToElement(new SuggestHabitsTool(Substitute.For<IMediator>()).GetParameterSchema());
        schema.GetProperty("properties").GetProperty("excluded_activities").GetProperty("type").GetString().Should().Be("array");
        schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Should().NotContain("excluded_activities");
    }

    [Theory]
    [InlineData("{\"count\":1}", false)]
    [InlineData("{\"count\":1,\"excluded_activities\":null}", false)]
    [InlineData("{\"count\":1,\"excluded_activities\":[]}", true)]
    public async Task SuggestTool_PreservesOmittedNullAndEmptyExclusions(string json, bool supplied)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new HabitSuggestionsCard("en", [Draft()])));
        using var args = JsonDocument.Parse(json);

        var result = await new SuggestHabitsTool(mediator).ExecuteAsync(args.RootElement, _userId, default);

        result.Success.Should().BeTrue();
        await mediator.Received(1).Send(Arg.Is<SuggestHabitDraftsQuery>(query =>
            supplied ? query.ExcludedActivities != null && query.ExcludedActivities.Count == 0 : query.ExcludedActivities == null),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("42")]
    [InlineData("\"Stretch\"")]
    [InlineData("[null]")]
    [InlineData("[42]")]
    [InlineData("[\" \"]")]
    [InlineData("[\"!!!\"]")]
    public async Task SuggestTool_RejectsMalformedExclusionsBeforeDispatch(string value)
    {
        var mediator = Substitute.For<IMediator>();
        using var args = JsonDocument.Parse("{\"count\":1,\"excluded_activities\":" + value + "}");

        var result = await new SuggestHabitsTool(mediator).ExecuteAsync(args.RootElement, _userId, default);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("HABIT_SUGGESTION_INVALID");
        await mediator.DidNotReceive().Send(Arg.Any<SuggestHabitDraftsQuery>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(101, 1)]
    [InlineData(1, 201)]
    [InlineData(1, 0)]
    public async Task ExclusionBounds_AreGuardedByValidatorAndService(int count, int length)
    {
        var excluded = Enumerable.Repeat(new string('x', length), count).ToArray();
        var query = new SuggestHabitDraftsQuery(_userId, "ideas", ExcludedActivities: excluded);
        new SuggestHabitDraftsQueryValidator().Validate(query).IsValid.Should().BeFalse();

        var result = await Service().GenerateAsync(_userId, "ideas", "en", 1, null, default, excluded);

        result.ErrorCode.Should().Be("HABIT_SUGGESTION_INVALID");
        await _generator.DidNotReceive().GenerateAsync(Arg.Any<HabitSuggestionRequest>(), Arg.Any<CancellationToken>());
    }
}
