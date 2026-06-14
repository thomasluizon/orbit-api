using FluentValidation;
using Orbit.Application.Common;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Validators;

public static class SharedHabitRules
{
    public static void AddTitleRules<T>(IRuleBuilder<T, string> rule)
    {
        rule.NotEmpty().MaximumLength(AppConstants.MaxHabitTitleLength);
    }

    public static void AddDescriptionRules<T>(IRuleBuilder<T, string?> rule)
    {
        rule.MaximumLength(AppConstants.MaxHabitDescriptionLength);
    }

    public static void AddEmojiRules<T>(IRuleBuilder<T, string?> rule)
    {
        rule.MaximumLength(AppConstants.MaxHabitEmojiLength);
    }

    public static void AddChecklistItemRules<T>(IRuleBuilder<T, IReadOnlyList<ChecklistItem>?> rule)
    {
        rule.Must(items => items is null || items.All(i => i.Text.Length <= AppConstants.MaxChecklistItemTextLength))
            .WithMessage($"Checklist item text must not exceed {AppConstants.MaxChecklistItemTextLength} characters");
    }

    public static void AddFrequencyQuantityRules<T>(IRuleBuilderOptions<T, int?> rule)
    {
        rule.GreaterThan(0);
    }

    public static void AddDaysRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<System.DayOfWeek>?>> daysExpr,
        System.Linq.Expressions.Expression<Func<T, int?>> freqQtyExpr,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr,
        System.Linq.Expressions.Expression<Func<T, bool>> isFlexibleExpr)
    {
        var freqQtyFunc = freqQtyExpr.Compile();
        var freqUnitFunc = freqUnitExpr.Compile();
        var isFlexibleFunc = isFlexibleExpr.Compile();

        validator.RuleFor(daysExpr)
            .Must((command, days) =>
            {
                if (days is null || days.Count == 0) return true;
                if (isFlexibleFunc(command)) return true;
                return freqQtyFunc(command) == 1 && freqUnitFunc(command) == Domain.Enums.FrequencyUnit.Day;
            })
            .WithMessage("Days can only be specified for a daily habit (frequency unit Day, quantity 1)");
    }

    public static void AddOneTimeTaskEndDateRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, DateOnly?>> endDateExpr,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr,
        System.Linq.Expressions.Expression<Func<T, bool>> isGeneralExpr)
    {
        var freqUnitFunc = freqUnitExpr.Compile();
        var isGeneralFunc = isGeneralExpr.Compile();

        validator.RuleFor(endDateExpr)
            .Must((command, endDate) =>
                !endDate.HasValue || freqUnitFunc(command) is not null || isGeneralFunc(command))
            .WithMessage("A one-time task cannot have an end date");
    }

    public static void AddGeneralHabitRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, bool>> isGeneralExpr,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr,
        System.Linq.Expressions.Expression<Func<T, int?>> freqQtyExpr,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<System.DayOfWeek>?>> daysExpr)
    {
        var isGeneralFunc = isGeneralExpr.Compile();
        AddGeneralHabitRulesCore(validator, x => isGeneralFunc(x), freqUnitExpr, freqQtyExpr, daysExpr);
    }

    public static void AddGeneralHabitRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, bool?>> isGeneralExpr,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr,
        System.Linq.Expressions.Expression<Func<T, int?>> freqQtyExpr,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<System.DayOfWeek>?>> daysExpr)
    {
        var isGeneralFunc = isGeneralExpr.Compile();
        AddGeneralHabitRulesCore(validator, x => isGeneralFunc(x) == true, freqUnitExpr, freqQtyExpr, daysExpr);
    }

    public static void AddScheduledReminderRules<T>(IRuleBuilder<T, IReadOnlyList<ScheduledReminderTime>?> rule)
    {
        rule.Must(items => items is null || items.Count <= AppConstants.MaxScheduledReminders)
            .WithMessage($"A habit can have at most {AppConstants.MaxScheduledReminders} scheduled reminders");

        rule.Must(items => items is null || items.All(sr => Enum.IsDefined(sr.When)))
            .WithMessage("Scheduled reminder 'when' must be 'day_before' or 'same_day'");

        rule.Must(items =>
            {
                if (items is null) return true;
                var grouped = items.GroupBy(sr => (sr.When, sr.Time));
                return grouped.All(g => g.Count() == 1);
            })
            .WithMessage("Scheduled reminders must not contain duplicate entries");
    }

    public static void AddReminderTimesRules<T>(IRuleBuilder<T, IReadOnlyList<int>?> rule)
    {
        rule.Must(times => times is null || times.Count <= AppConstants.MaxReminderTimes)
            .WithMessage($"A habit can have at most {AppConstants.MaxReminderTimes} reminder times");

        rule.Must(times => times is null || times.All(m => m is >= 0 and <= AppConstants.MaxReminderMinutesBefore))
            .WithMessage($"Reminder times must be between 0 and {AppConstants.MaxReminderMinutesBefore} minutes before the due time");

        rule.Must(times => times is null || times.Distinct().Count() == times.Count)
            .WithMessage("Reminder times must not contain duplicate values");
    }

    public static void AddGoalIdsRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<Guid>?>> expression)
    {
        validator.RuleFor(expression)
            .Must(ids => ids is null || ids.Count <= AppConstants.MaxGoalsPerHabit)
            .WithMessage($"A habit can have at most {AppConstants.MaxGoalsPerHabit} linked goals.");
    }

    private static void AddGeneralHabitRulesCore<T>(
        AbstractValidator<T> validator,
        Func<T, bool> isGeneralPredicate,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr,
        System.Linq.Expressions.Expression<Func<T, int?>> freqQtyExpr,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<System.DayOfWeek>?>> daysExpr)
    {
        validator.RuleFor(freqUnitExpr)
            .Null()
            .When(isGeneralPredicate)
            .WithMessage("General habits cannot have a frequency unit");

        validator.RuleFor(freqQtyExpr)
            .Null()
            .When(isGeneralPredicate)
            .WithMessage("General habits cannot have a frequency quantity");

        validator.RuleFor(daysExpr)
            .Must(days => days is null || days.Count == 0)
            .When(isGeneralPredicate)
            .WithMessage("General habits cannot have specific days");
    }
}
