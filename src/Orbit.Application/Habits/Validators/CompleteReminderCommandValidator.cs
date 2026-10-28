using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public sealed class CompleteReminderCommandValidator : AbstractValidator<CompleteReminderCommand>
{
    public CompleteReminderCommandValidator() => RuleFor(x => x.Token).NotEmpty().MaximumLength(2048)
        .WithCopy("INVALID_REMINDER_ACTION");
}
