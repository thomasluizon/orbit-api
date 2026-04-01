using Orbit.Domain.Common;

namespace Orbit.Application.Common;

public static class AppConstants
{
    public const int MaxSubHabits = 20;
    public const int MaxHabitTitleLength = 200;
    public const int MaxHabitDescriptionLength = 2000;
    public const int MaxChecklistItemTextLength = 500;
    public const int MaxHabitDepth = 5;
    public const int MaxTagsPerHabit = 5;
    public const int MaxUserFacts = 50;
    public const int MaxRangeDays = 366;
    public const int DefaultReminderMinutes = 15;
    public const int DefaultFreeMaxHabits = 10;
    public const int DefaultFreeAiMessages = 20;
    public const int DefaultProAiMessages = 500;
    public const int MaxBulkOperationSize = 100;
    public const int MaxGoalsPerHabit = 10;
    public const int MaxHabitsPerGoal = 20;
    public const int ReferralDiscountPercent = 10;
    public const int DefaultMaxReferrals = 10;
    public const int ReferralCompletionThreshold = 3;
    public const int ReferralCompletionWindowDays = 7;
    public const int DefaultOverdueWindowDays = 7;
    public const int MaxScheduledReminders = DomainConstants.MaxScheduledReminders;
    public const int MaxPushSubscriptionsPerUser = 5;
    public const int MaxNotificationsReturned = 50;
    public const int MaxChecklistItems = 50;
    public const int MaxTagNameLength = 50;
    public const int MaxLanguageLength = 10;
    public static readonly string[] SupportedLanguages = ["en", "pt-BR"];
}
