using FluentValidation;

namespace Orbit.Application.Gamification.Queries;

public class GetRecapQueryValidator : AbstractValidator<GetRecapQuery>
{
    private static readonly string[] AllowedPeriods = ["week", "month", "year"];

    public GetRecapQueryValidator()
    {
        RuleFor(x => x.Period)
            .NotEmpty()
            .Must(period => AllowedPeriods.Contains(period, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Period must be one of: week, month, year.");

        RuleFor(x => x.DateFrom)
            .LessThanOrEqualTo(x => x.DateTo)
            .WithMessage("DateFrom must be on or before DateTo.");
    }
}
