using FluentValidation;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Chat.Validators;

public sealed class HabitSuggestionDraftValidator : AbstractValidator<HabitSuggestionDraft>
{
    public HabitSuggestionDraftValidator()
    {
        RuleFor(draft => draft).Must(draft => draft.ToHabit(Guid.NewGuid()).IsSuccess)
            .WithMessage("Invalid habit suggestion.").WithErrorCode("HABIT_SUGGESTION_INVALID");
    }
}

public sealed class SuggestHabitDraftsQueryValidator : AbstractValidator<SuggestHabitDraftsQuery>
{
    public SuggestHabitDraftsQueryValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.Instruction).NotEmpty().MaximumLength(1000);
        RuleFor(request => request.Language).Must(HabitSuggestionValidation.IsLanguage);
        RuleFor(request => request.Count).InclusiveBetween(1, 5);
    }
}

public sealed class RefineHabitSuggestionQueryValidator : AbstractValidator<RefineHabitSuggestionQuery>
{
    public RefineHabitSuggestionQueryValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.Instruction).NotEmpty().MaximumLength(500);
        RuleFor(request => request.Language).Must(HabitSuggestionValidation.IsLanguage);
        RuleFor(request => request.Draft).NotNull().SetValidator(new HabitSuggestionDraftValidator());
    }
}

public sealed class PreviewHabitSuggestionCommandValidator : AbstractValidator<PreviewHabitSuggestionCommand>
{
    public PreviewHabitSuggestionCommandValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.Language).Must(HabitSuggestionValidation.IsLanguage);
        RuleFor(request => request.Draft).NotNull().SetValidator(new HabitSuggestionDraftValidator());
    }
}

internal static class HabitSuggestionValidation
{
    internal static bool IsLanguage(string language) => language is "en" or "pt" or "pt-BR";
}
