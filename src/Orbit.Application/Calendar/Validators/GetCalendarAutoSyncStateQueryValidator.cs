using FluentValidation;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Validators;

public class GetCalendarAutoSyncStateQueryValidator : AbstractValidator<GetCalendarAutoSyncStateQuery>
{
    public GetCalendarAutoSyncStateQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
