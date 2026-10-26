using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public sealed class AppendChecklistItemCommandValidator : AbstractValidator<AppendChecklistItemCommand>
{
    public AppendChecklistItemCommandValidator()
    {
        RuleFor(command => command.UserId).NotEmpty();
        RuleFor(command => command.HabitId).NotEmpty();
        RuleFor(command => command.Text).NotEmpty().MaximumLength(AppConstants.MaxChecklistItemTextLength);
    }
}
