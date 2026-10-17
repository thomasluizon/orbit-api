using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class UpdateChecklistCommandValidator : AbstractValidator<UpdateChecklistCommand>
{
    public UpdateChecklistCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

        RuleFor(x => x.ChecklistItems)
            .NotNull();

        RuleFor(x => x.ChecklistItems)
            .Must(items => items is null || items.Count <= AppConstants.MaxChecklistItems)
            .WithCopy(ValidationErrorCodes.ChecklistItemLimit);

        RuleFor(x => x.ChecklistItems)
            .Must(items => items is null || items.All(i => i.Text.Length <= AppConstants.MaxChecklistItemTextLength))
            .WithCopy(ValidationErrorCodes.ChecklistItemLength);
    }
}
