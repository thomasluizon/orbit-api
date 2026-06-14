using FluentValidation;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class SetWeekStartDayCommandValidator : AbstractValidator<SetWeekStartDayCommand>
{
    public SetWeekStartDayCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.WeekStartDay)
            .InclusiveBetween(0, 1)
            .WithMessage("WeekStartDay must be 0 (Sunday) or 1 (Monday).");
    }
}
