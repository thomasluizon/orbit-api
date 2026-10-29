using FluentValidation;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Queries;
using Orbit.Domain.Models;
using Orbit.Application.Common;

namespace Orbit.Application.Chat.Validators;

public sealed class HabitSuggestionDraftValidator : AbstractValidator<HabitSuggestionDraft>
{
    public HabitSuggestionDraftValidator()
    {
        RuleFor(draft => draft).Must(draft => draft.ToHabit(Guid.NewGuid()).IsSuccess)
            .WithCopy("HABIT_SUGGESTION_INVALID");
    }
}

public sealed class SuggestHabitDraftsQueryValidator : AbstractValidator<SuggestHabitDraftsQuery>
{
    public SuggestHabitDraftsQueryValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.Instruction).NotEmpty().WithCopy(ValidationErrorCodes.HabitSuggestionInstruction)
            .MaximumLength(1000).WithCopy(ValidationErrorCodes.HabitSuggestionInstruction);
        RuleFor(request => request.Language).Must(HabitSuggestionValidation.IsLanguage).WithCopy(ValidationErrorCodes.LanguageSupported);
        RuleFor(request => request.Count).Must(HabitSuggestionsCard.IsValidDraftCount).WithCopy(ValidationErrorCodes.HabitSuggestionCount);
        RuleFor(request => request.ExcludedActivities).Must(HabitSuggestionRequest.HasValidExclusions).WithCopy("HABIT_SUGGESTION_INVALID");
    }
}

public sealed class RefineHabitSuggestionQueryValidator : AbstractValidator<RefineHabitSuggestionQuery>
{
    public RefineHabitSuggestionQueryValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.Instruction).NotEmpty().WithCopy(ValidationErrorCodes.HabitSuggestionInstruction)
            .MaximumLength(500).WithCopy(ValidationErrorCodes.HabitSuggestionInstruction);
        RuleFor(request => request.Language).Must(HabitSuggestionValidation.IsLanguage).WithCopy(ValidationErrorCodes.LanguageSupported);
        RuleFor(request => request.Draft).NotNull().WithCopy("HABIT_SUGGESTION_INVALID").SetValidator(new HabitSuggestionDraftValidator());
    }
}

public sealed class PreviewHabitSuggestionCommandValidator : AbstractValidator<PreviewHabitSuggestionCommand>
{
    public PreviewHabitSuggestionCommandValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.Language).Must(HabitSuggestionValidation.IsLanguage).WithCopy(ValidationErrorCodes.LanguageSupported);
        RuleFor(request => request.Draft).NotNull().WithCopy("HABIT_SUGGESTION_INVALID").SetValidator(new HabitSuggestionDraftValidator());
    }
}

public sealed class PreviewAllHabitSuggestionsCommandValidator : AbstractValidator<PreviewAllHabitSuggestionsCommand>
{
    public PreviewAllHabitSuggestionsCommandValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.Language).Must(HabitSuggestionValidation.IsLanguage).WithCopy(ValidationErrorCodes.LanguageSupported);
        RuleFor(request => request.Drafts).Cascade(CascadeMode.Stop).NotNull().WithCopy(ValidationErrorCodes.HabitSuggestionCount)
            .Must(drafts => HabitSuggestionsCard.IsValidDraftCount(drafts.Count)).WithCopy(ValidationErrorCodes.HabitSuggestionCount);
        RuleForEach(request => request.Drafts).NotNull().WithCopy("HABIT_SUGGESTION_INVALID").SetValidator(new HabitSuggestionDraftValidator());
    }
}

internal static class HabitSuggestionValidation
{
    internal static bool IsLanguage(string language) => language is "en" or "pt" or "pt-BR";
}
