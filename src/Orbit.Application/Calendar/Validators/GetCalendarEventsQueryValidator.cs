using FluentValidation;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Validators;

public class GetCalendarEventsQueryValidator : AbstractValidator<GetCalendarEventsQuery>
{
    public GetCalendarEventsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.CalendarIds).Must(ids => ids is null || ids.Count <= 200 && ids.All(id => !string.IsNullOrWhiteSpace(id)));

    }
}
