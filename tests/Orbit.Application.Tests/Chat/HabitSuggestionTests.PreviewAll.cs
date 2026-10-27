using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Chat;

public partial class HabitSuggestionTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void PreviewAllValidator_UsesSuggestionBlockBounds(int count, bool valid)
    {
        var drafts = Enumerable.Range(0, count).Select(index => Draft($"Habit {index}")).ToArray();
        new PreviewAllHabitSuggestionsCommandValidator().Validate(new PreviewAllHabitSuggestionsCommand(_userId, drafts)).IsValid.Should().Be(valid);
    }

    [Fact]
    public void PreviewAllValidator_RejectsMissingOrInvalidFieldsWithIndexedDraftErrors()
    {
        var validator = new PreviewAllHabitSuggestionsCommandValidator();
        validator.Validate(new PreviewAllHabitSuggestionsCommand(_userId, null!)).IsValid.Should().BeFalse();
        validator.Validate(new PreviewAllHabitSuggestionsCommand(Guid.Empty, [Draft()])).IsValid.Should().BeFalse();
        validator.Validate(new PreviewAllHabitSuggestionsCommand(_userId, [Draft()], "fr")).IsValid.Should().BeFalse();
        validator.Validate(new PreviewAllHabitSuggestionsCommand(_userId, [Draft(), null!])).Errors.Should().Contain(error => error.PropertyName == "Drafts[1]");
        validator.Validate(new PreviewAllHabitSuggestionsCommand(_userId, [Draft(), Draft() with { DueEndTime = new(20, 0) }])).Errors
            .Should().Contain(error => error.PropertyName == "Drafts[1]" && error.ErrorCode == "HABIT_SUGGESTION_INVALID");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PreviewAll_ChecksEveryDraftBeforeCallingExecutor(int failingIndex)
    {
        var drafts = Enumerable.Range(0, 3).Select(index => Draft($"Habit {index}") with
        { DueTime = new(18 + index, 0), DueEndTime = new(18 + index, 15) }).ToArray();
        drafts[failingIndex] = drafts[failingIndex] with { ExistingHabitId = Guid.NewGuid() };
        var executor = Substitute.For<IAgentOperationExecutor>();
        var store = Substitute.For<IPendingAgentOperationStore>();
        var changes = new AccountChangeSetService(store, Substitute.For<IPendingOperationChangePreviewer>(),
            Substitute.For<IAgentCatalogService>(), new AiToolRegistry([]));
        var result = await new PreviewAllHabitSuggestionsCommandHandler(Service(), executor, changes, store)
            .Handle(new(_userId, drafts), default);
        result.Should().BeOfType<HabitSuggestionPreviewFailure>().Which.DraftIndex.Should().Be(failingIndex);
        result.ErrorCode.Should().Be("HABIT_SUGGESTION_UNAVAILABLE");
        executor.ReceivedCalls().Should().BeEmpty();
        store.ReceivedCalls().Should().BeEmpty();
    }
}
