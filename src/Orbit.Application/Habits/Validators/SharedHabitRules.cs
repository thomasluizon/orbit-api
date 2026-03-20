using FluentValidation;
using Orbit.Application.Common;

namespace Orbit.Application.Habits.Validators;

public static class SharedHabitRules
{
    public static void AddTitleRules<T>(IRuleBuilder<T, string> rule)
    {
        rule.NotEmpty().MaximumLength(AppConstants.MaxHabitTitleLength);
    }

    public static void AddFrequencyQuantityRules<T>(IRuleBuilderOptions<T, int?> rule)
    {
        rule.GreaterThan(0);
    }

    public static void AddDaysRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<System.DayOfWeek>?>> daysExpr,
        System.Linq.Expressions.Expression<Func<T, int?>> freqExpr)
    {
        validator.RuleFor(daysExpr)
            .Must((command, days) =>
            {
                if (days is null || days.Count == 0) return true;
                var freq = freqExpr.Compile()(command);
                return freq == 1;
            })
            .WithMessage("Days can only be specified when frequency quantity is 1");
    }
}
