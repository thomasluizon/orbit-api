using FluentValidation;
using Orbit.Application.Habits.Commands;

namespace Orbit.Application.Habits.Validators;

public class LogHabitCommandValidator : AbstractValidator<LogHabitCommand>
{
    public LogHabitCommandValidator()
    {
        RuleFor(x => x.TakenAtUtc)
            .Must(value => value is null || Orbit.Domain.Entities.HabitLog.IsValidTakenAtUtc(value.Value, DateTime.UtcNow))
            .WithMessage("Choose a dose instant in UTC that is not in the future.")
            .WithErrorCode(Orbit.Domain.Common.DomainErrors.InvalidTakenAtUtc.Code);
        RuleFor(x => x.UserId)
            .NotEmpty();

        RuleFor(x => x.HabitId)
            .NotEmpty();

    }
}
