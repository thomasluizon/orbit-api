using FluentValidation;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Services;

namespace Orbit.Application.Habits.Validators;

public sealed class GetPeriodAnalyticsQueryValidator : AbstractValidator<GetPeriodAnalyticsQuery>
{
    public GetPeriodAnalyticsQueryValidator()
    {
        RuleFor(q => q.UserId).NotEmpty();
        RuleFor(q => q.HabitId).NotEqual(Guid.Empty).When(q => q.HabitId.HasValue);
        RuleFor(q => q.Metric).Must(PeriodAnalyticsCalculator.Metrics.Contains);
        RuleFor(q => q.DateTo).GreaterThanOrEqualTo(q => q.DateFrom)
            .Must((q, to) => to.DayNumber - q.DateFrom.DayNumber <= 3659);
        RuleFor(q => q.BaselineTo).Must((q, to) => q.BaselineFrom.HasValue == to.HasValue
            && (!to.HasValue || to >= q.BaselineFrom && to.Value.DayNumber - q.BaselineFrom!.Value.DayNumber <= 3659));
        RuleFor(q => q.Search).MaximumLength(200);
        RuleFor(q => q.Tag).MaximumLength(200);
    }
}
