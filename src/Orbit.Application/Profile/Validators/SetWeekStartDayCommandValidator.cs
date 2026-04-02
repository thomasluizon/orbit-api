using FluentValidation;
using Orbit.Application.Profile.Commands;

namespace Orbit.Application.Profile.Validators;

public class SetWeekStartDayCommandValidator : AbstractValidator<SetWeekStartDayCommand>
{
    public SetWeekStartDayCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.WeekStartDay)
            .InclusiveBetween(0, 6)
            .WithMessage("WeekStartDay must be between 0 (Sunday) and 6 (Saturday).");
    }
}
