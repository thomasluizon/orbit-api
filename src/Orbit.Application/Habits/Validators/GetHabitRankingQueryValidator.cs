using FluentValidation;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Validators;

public sealed class GetHabitRankingQueryValidator : AbstractValidator<GetHabitRankingQuery>
{
    public GetHabitRankingQueryValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.QueryToken).MaximumLength(16384);
        RuleFor(x => x.Cursor).MaximumLength(2048);
        RuleFor(x => x).Must(x => x.QueryToken is null
            ? x.Cursor is null && !x.Refresh
            : !string.IsNullOrWhiteSpace(x.QueryToken) && x.DateFrom is null && x.DateToExclusive is null
                && x.Window is null && x.Months is null && x.Metric is null && x.HabitIds is null
                && x.PageSize is null && (x.Refresh ? x.Cursor is null : !string.IsNullOrWhiteSpace(x.Cursor)))
            .WithMessage("Pages and refresh must use only the bound query token and cursor or refresh flag.");
        When(x => x.QueryToken is null, () =>
        {
            RuleFor(x => x).Must(x => x.Window is null
                ? x.Months is null && x.DateFrom.HasValue && x.DateToExclusive.HasValue
                : x.DateFrom is null && x.DateToExclusive is null
                    && x.Window is "rolling_months" or "complete_months" && x.Months is >= 1 and <= 120)
                .WithMessage("Provide absolute dates or a calendar-month window and month count.");
            RuleFor(x => x).Must(x => x.DateFrom is null || x.DateToExclusive is null
                || (x.DateFrom < x.DateToExclusive
                    && x.DateToExclusive.Value.DayNumber - x.DateFrom.Value.DayNumber <= HabitRankingDefinition.MaxWindowDays))
                .WithMessage("Invalid ranking date range.");
            RuleFor(x => x.Metric).Must(x => x is null or HabitRankingDefinition.CompletionLogRecords);
            RuleFor(x => x.PageSize).Must(x => x is null or >= 1 and <= HabitRankingDefinition.MaxPageSize);
            RuleFor(x => x.HabitIds).Must(x => x is null || x.Count <= HabitRankingDefinition.MaxHabitIds
                && !x.Contains(Guid.Empty));
        });
    }
}
