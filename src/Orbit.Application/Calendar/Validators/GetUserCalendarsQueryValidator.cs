using FluentValidation;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Validators;

public class GetUserCalendarsQueryValidator : AbstractValidator<GetUserCalendarsQuery>
{
    public GetUserCalendarsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
