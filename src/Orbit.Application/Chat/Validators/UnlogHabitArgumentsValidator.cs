using FluentValidation;

namespace Orbit.Application.Chat.Validators;

public sealed record UnlogHabitArguments(Guid UserId, Guid HabitId);

public sealed class UnlogHabitArgumentsValidator : AbstractValidator<UnlogHabitArguments>
{
    public UnlogHabitArgumentsValidator()
    {
        RuleFor(args => args.UserId).NotEmpty();
        RuleFor(args => args.HabitId).NotEmpty().WithMessage("habit_id is required and must be a valid GUID.");
    }
}
