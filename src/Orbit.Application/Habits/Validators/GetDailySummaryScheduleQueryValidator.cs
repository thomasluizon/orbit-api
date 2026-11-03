using FluentValidation;
using Orbit.Application.Common;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Habits.Validators;

public sealed class GetDailySummaryScheduleQueryValidator : AbstractValidator<GetDailySummaryScheduleQuery>
{
    public GetDailySummaryScheduleQueryValidator()
    {
        RuleFor(q => q.UserId).NotEmpty();
        RuleFor(q => q.DateTo).GreaterThanOrEqualTo(q => q.DateFrom).WithCopy(ValidationErrorCodes.DateRangeOrderLower);
        RuleFor(q => q).Must(q => q.DateTo.DayNumber - q.DateFrom.DayNumber <= AppConstants.MaxReadRangeDays)
            .WithCopy(ValidationErrorCodes.DateRangeLimitShort).When(q => q.DateTo >= q.DateFrom);
        RuleFor(q => q.Habits).Must((q, habits) => habits.All(h => h.UserId == q.UserId));
    }
}
