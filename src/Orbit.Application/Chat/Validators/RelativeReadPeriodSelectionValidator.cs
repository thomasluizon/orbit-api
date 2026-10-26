using FluentValidation;
using Orbit.Application.Habits.Queries;

namespace Orbit.Application.Chat.Validators;

public sealed record RelativeReadPeriodSelection(string Period, int? Count);

public sealed class RelativeReadPeriodSelectionValidator : AbstractValidator<RelativeReadPeriodSelection>
{
    public RelativeReadPeriodSelectionValidator()
    {
        RuleFor(selection => selection.Period).Must(RetrospectivePeriodRange.IsRelativePeriod)
            .WithMessage("Unknown relative period.");
        RuleFor(selection => selection.Count).Must(ValidCount)
            .WithMessage("period_count requires a bounded positive count for last_n_days or last_n_weeks only.");
    }

    private static bool ValidCount(RelativeReadPeriodSelection selection, int? count) =>
        selection.Period?.ToLowerInvariant() switch
        {
            "last_n_days" => count is >= 1 and <= 3659,
            "last_n_weeks" => count is >= 1 and <= 522,
            _ => !count.HasValue
        };
}
