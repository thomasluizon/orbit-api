using FluentValidation;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Validators;

public class GetCalendarEventsQueryValidator : AbstractValidator<GetCalendarEventsQuery>
{
    public GetCalendarEventsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
