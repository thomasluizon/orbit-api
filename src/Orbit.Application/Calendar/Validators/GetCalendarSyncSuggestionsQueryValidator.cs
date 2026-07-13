using FluentValidation;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Validators;

public class GetCalendarSyncSuggestionsQueryValidator : AbstractValidator<GetCalendarSyncSuggestionsQuery>
{
    public GetCalendarSyncSuggestionsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
    }
}
