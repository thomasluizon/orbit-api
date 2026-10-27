using FluentAssertions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Validators;

namespace Orbit.Application.Tests.Chat;

public partial class HabitSuggestionTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task UntimedDraft_DoesNotReserveTimeAgainstHabitsOrOtherDrafts(string language)
    {
        var untimed = Draft("Read") with { DueTime = null, DueEndTime = null };
        var timed = Draft("Stretch");
        Generate(untimed, timed);

        var result = await Service().GenerateAsync(_userId, "ideas", language, 2, null, default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.Drafts.Should().Equal(untimed, timed);
        var withExisting = await Service(Draft("Work").ToHabit(_userId).Value,
                (Draft("Walk") with { DueTime = null, DueEndTime = null }).ToHabit(_userId).Value)
            .CheckForPreviewAsync(_userId, untimed, language, default);
        withExisting.IsSuccess.Should().BeTrue(withExisting.Error);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void DraftValidators_RequireBothTimesOrNeither(bool start, bool end, bool valid)
    {
        var draft = Draft() with { DueTime = start ? new(22, 0) : null, DueEndTime = end ? new(22, 15) : null };
        new HabitSuggestionDraftValidator().Validate(draft).IsValid.Should().Be(valid);
        new PreviewHabitSuggestionCommandValidator().Validate(new PreviewHabitSuggestionCommand(_userId, draft)).IsValid.Should().Be(valid);
        new PreviewAllHabitSuggestionsCommandValidator().Validate(new PreviewAllHabitSuggestionsCommand(_userId, [draft])).IsValid.Should().Be(valid);
        new RefineHabitSuggestionQueryValidator().Validate(new RefineHabitSuggestionQuery(_userId, draft, "easier")).IsValid.Should().Be(valid);
    }
}
