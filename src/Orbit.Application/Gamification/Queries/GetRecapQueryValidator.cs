using FluentValidation;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Gamification.Queries;

public class GetRecapQueryValidator : AbstractValidator<GetRecapQuery>
{
    public GetRecapQueryValidator()
    {
        RuleFor(x => x.Period)
            .NotEmpty()
            .Must(period => RetrospectivePeriodRange.IsKnownPeriod(period))
            .WithMessage("Period must be one of: week, month, quarter, semester, year.");

        RuleFor(x => x.DateFrom)
            .LessThanOrEqualTo(x => x.DateTo)
            .WithMessage("DateFrom must be on or before DateTo.");

        RuleFor(x => x)
            .Must(HasValidClosedParameters)
            .WithMessage("Closed period parameters must match the requested period.");

        When(x => x.ClosedYear.HasValue && x.ClosedMonth.HasValue, () =>
        {
            RuleFor(x => x.Period)
                .Equal("month", StringComparer.OrdinalIgnoreCase)
                .WithMessage("Closed calendar parameters can only be used with the month period.");

            RuleFor(x => x.ClosedYear)
                .InclusiveBetween(1, 9999)
                .WithMessage("ClosedYear must be between 1 and 9999.");

            RuleFor(x => x.ClosedMonth)
                .InclusiveBetween(1, 12)
                .WithMessage("ClosedMonth must be between 1 and 12.");

            RuleFor(x => x)
                .Must(x => MatchesClosedMonth(x.DateFrom, x.DateTo, x.ClosedYear!.Value, x.ClosedMonth!.Value))
                .When(x => x.ClosedYear is >= 1 and <= 9999 && x.ClosedMonth is >= 1 and <= 12)
                .WithMessage("DateFrom and DateTo must match the complete closed calendar month.");
        });

        When(x => x.ClosedYear.HasValue && !x.ClosedMonth.HasValue, () =>
        {
            RuleFor(x => x.ClosedYear)
                .InclusiveBetween(1, 9999)
                .WithMessage("ClosedYear must be between 1 and 9999.");

            RuleFor(x => x)
                .Must(x => x.ClosedYear is >= 1 and <= 9999
                    && x.DateFrom == new DateOnly(x.ClosedYear.Value, 1, 1)
                    && x.DateTo == new DateOnly(x.ClosedYear.Value, 12, 31))
                .WithMessage("DateFrom and DateTo must match the complete closed calendar year.");
        });

        When(x => x.ClosedWeekStart.HasValue, () =>
        {
            RuleFor(x => x)
                .Must(x => x.ClosedWeekStart is { } weekStart
                    && weekStart.DayNumber <= DateOnly.MaxValue.DayNumber - 6
                    && x.DateFrom == weekStart
                    && x.DateTo == weekStart.AddDays(6))
                .WithMessage("DateFrom and DateTo must match the complete closed calendar week.");
        });
    }

    private static bool HasValidClosedParameters(GetRecapQuery query)
    {
        if (query.ClosedWeekStart.HasValue)
            return string.Equals(query.Period, "week", StringComparison.OrdinalIgnoreCase)
                && !query.ClosedYear.HasValue && !query.ClosedMonth.HasValue;

        if (query.ClosedMonth.HasValue)
            return string.Equals(query.Period, "month", StringComparison.OrdinalIgnoreCase)
                && query.ClosedYear.HasValue;

        return !query.ClosedYear.HasValue
            || string.Equals(query.Period, "year", StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesClosedMonth(DateOnly dateFrom, DateOnly dateTo, int year, int month) =>
        dateFrom == new DateOnly(year, month, 1)
        && dateTo == new DateOnly(year, month, DateTime.DaysInMonth(year, month));
}
