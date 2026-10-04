using FluentValidation;
using Orbit.Application.Common;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Habits.Validators;

public static class SharedHabitRules
{
    public static void AddTitleRules<T>(
        IRuleBuilder<T, string> rule,
        string? requiredCode = null,
        string? maximumLengthCode = null)
    {
        var requiredRule = rule.NotEmpty();
        if (requiredCode is not null)
            requiredRule.WithCopy(requiredCode);

        var lengthRule = requiredRule.MaximumLength(AppConstants.MaxHabitTitleLength);
        if (maximumLengthCode is not null)
            lengthRule.WithCopy(maximumLengthCode);
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
            .WithCopy(ValidationErrorCodes.ChecklistItemLength);
    }

    public static void AddFrequencyQuantityRules<T>(IRuleBuilderOptions<T, int?> rule)
    {
        rule.GreaterThan(0);
    }

    public static void AddIntervalWeeksRules<T>(IRuleBuilder<T, int?> rule)
    {
        rule.InclusiveBetween(1, AppConstants.MaxIntervalWeeks);
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
            .WithCopy(ValidationErrorCodes.DaysDailyOnly);
    }

    public static void AddOneTimeTaskEndDateRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, DateOnly?>> endDateExpr,
        System.Linq.Expressions.Expression<Func<T, Domain.Enums.FrequencyUnit?>> freqUnitExpr)
    {
        var freqUnitFunc = freqUnitExpr.Compile();
        validator.RuleFor(endDateExpr)
            .Must((command, endDate) =>
                !endDate.HasValue || freqUnitFunc(command) is not null)
            .WithCopy(ValidationErrorCodes.EndDateRecurringOnly);
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
            .WithCopy(ValidationErrorCodes.ScheduledReminderLimit);

        rule.Must(items => items is null || items.All(sr => Enum.IsDefined(sr.When)))
            .WithCopy(ValidationErrorCodes.ScheduledReminderWhen);

        rule.Must(items =>
            {
                if (items is null) return true;
                var grouped = items.GroupBy(sr => (sr.When, sr.Time));
                return grouped.All(g => g.Count() == 1);
            })
            .WithCopy(ValidationErrorCodes.ScheduledRemindersUnique);
    }

    public static void AddReminderTimesRules<T>(IRuleBuilder<T, IReadOnlyList<int>?> rule)
    {
        rule.Must(times => times is null || times.Count <= AppConstants.MaxReminderTimes)
            .WithCopy(ValidationErrorCodes.ReminderTimeLimit);

        rule.Must(times => times is null || times.All(m => m is >= 0 and <= AppConstants.MaxReminderMinutesBefore))
            .WithCopy(ValidationErrorCodes.ReminderTimeRange);

        rule.Must(times => times is null || times.Distinct().Count() == times.Count)
            .WithCopy(ValidationErrorCodes.ReminderTimesUnique);
    }

    public static void AddRelativeReminderRules<T>(IRuleBuilder<T, IReadOnlyList<RelativeReminderTime>?> rule)
    {
        rule.Must(reminders => reminders is null ||
            (reminders.Count <= AppConstants.MaxRelativeReminders
            && reminders.Distinct().Count() == reminders.Count
            && reminders.All(r => (r.MinutesBefore.HasValue != r.When.HasValue)
                && (r.When.HasValue == r.Time.HasValue)
                && (!r.MinutesBefore.HasValue || r.MinutesBefore.Value is >= -1439 and <= AppConstants.MaxReminderMinutesBefore)
                && (!r.When.HasValue || Enum.IsDefined(r.When.Value)))))
            .WithCopy(ValidationErrorCodes.RelativeRemindersValid);
    }

    public static void AddGoalIdsRules<T>(
        AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<Guid>?>> expression)
    {
        validator.RuleFor(expression)
            .Must(ids => ids is null || ids.Count <= AppConstants.MaxGoalsPerHabit)
            .WithCopy(ValidationErrorCodes.HabitGoalLimit);
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
            .WithCopy(ValidationErrorCodes.GeneralFrequencyUnitAbsent);

        validator.RuleFor(freqQtyExpr)
            .Null()
            .When(isGeneralPredicate)
            .WithCopy(ValidationErrorCodes.GeneralFrequencyQuantityAbsent);

        validator.RuleFor(daysExpr)
            .Must(days => days is null || days.Count == 0)
            .When(isGeneralPredicate)
            .WithCopy(ValidationErrorCodes.GeneralDaysAbsent);
    }
}
