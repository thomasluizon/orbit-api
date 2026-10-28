using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Habits.Validators;

public sealed class CorrectHabitDoseCommandValidator : AbstractValidator<CorrectHabitDoseCommand>
{
    public CorrectHabitDoseCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.HabitId).NotEmpty();
        RuleFor(x => x.LogId).NotEmpty();
        RuleFor(x => x.TakenAtUtc).Must(value => HabitLog.IsValidTakenAtUtc(value, DateTime.UtcNow))
            .WithCopy(DomainErrors.InvalidTakenAtUtc.Code);
    }
}
