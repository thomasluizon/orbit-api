using FluentValidation;
using Orbit.Application.Calendar.Queries;

namespace Orbit.Application.Calendar.Validators;

public class GetCalendarSyncSuggestionsQueryValidator : AbstractValidator<GetCalendarSyncSuggestionsQuery>
{
    public GetCalendarSyncSuggestionsQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.DateTo).GreaterThanOrEqualTo(x => x.AsOf).When(x => x.DateTo.HasValue && x.AsOf.HasValue);
        RuleFor(x => x.CalendarIds).Must(ids => ids is null || ids.Count <= 200 && ids.All(id => !string.IsNullOrWhiteSpace(id)));
    }
}
