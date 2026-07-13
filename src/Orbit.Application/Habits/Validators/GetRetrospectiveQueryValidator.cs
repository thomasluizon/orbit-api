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
            .WithMessage("Period must be one of: week, month, quarter, semester, year.");

        RuleFor(x => x.DateFrom)
            .LessThanOrEqualTo(x => x.DateTo)
            .WithMessage("DateFrom must be on or before DateTo.");

        RuleFor(x => x)
            .Must(x => x.DateTo.DayNumber - x.DateFrom.DayNumber <= AppConstants.MaxRangeDays)
            .WithMessage($"Date range must not exceed {AppConstants.MaxRangeDays} days.")
            .When(x => x.DateFrom <= x.DateTo);

        RuleFor(x => x.Language)
            .MaximumLength(AppConstants.MaxLanguageLength)
            .Must(lang => string.IsNullOrEmpty(lang) || AppConstants.SupportedLanguages.Contains(lang))
            .WithMessage($"Language must be one of: {string.Join(", ", AppConstants.SupportedLanguages)}");
    }
}
