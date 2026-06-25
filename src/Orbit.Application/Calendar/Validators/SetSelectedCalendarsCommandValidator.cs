using FluentValidation;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.Calendar.Validators;

public class SetSelectedCalendarsCommandValidator : AbstractValidator<SetSelectedCalendarsCommand>
{
    public SetSelectedCalendarsCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        RuleFor(x => x.CalendarIds)
            .NotNull()
            .Must(ids => ids.Count <= AppConstants.MaxSelectedCalendars)
            .WithMessage($"You can select at most {AppConstants.MaxSelectedCalendars} calendars.");

        RuleForEach(x => x.CalendarIds)
            .NotEmpty()
            .WithMessage("Calendar ids must be non-empty.");
    }
}
