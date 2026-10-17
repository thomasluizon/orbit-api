using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public class GetRetrospectiveQueryValidator : AbstractValidator<GetRetrospectiveQuery>
{
    public GetRetrospectiveQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();

        RuleFor(x => x.Period)
            .NotEmpty()
            .Must(period => RetrospectivePeriodRange.IsKnownPeriod(period))
            .WithCopy(ValidationErrorCodes.PeriodSupported);

        RuleFor(x => x.DateFrom)
            .LessThanOrEqualTo(x => x.DateTo)
            .WithCopy(ValidationErrorCodes.DateRangeOrder);

        RuleFor(x => x)
            .Must(x => x.DateTo.DayNumber - x.DateFrom.DayNumber <= AppConstants.MaxRangeDays)
            .WithCopy(ValidationErrorCodes.DateRangeLimit)
            .When(x => x.DateFrom <= x.DateTo);

        RuleFor(x => x.Language)
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => string.IsNullOrEmpty(lang) || AppConstants.SupportedLanguages.Contains(lang))
            .WithCopy(ValidationErrorCodes.LanguageSupported);
    }
}
