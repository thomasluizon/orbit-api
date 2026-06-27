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
    }
}
