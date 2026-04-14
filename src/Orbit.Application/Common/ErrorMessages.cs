namespace Orbit.Application.Common;

public static class ErrorMessages
{
    public const string UserNotFound = "User not found.";
    public const string HabitNotFound = "Habit not found.";
    public const string ParentHabitNotFound = "Parent habit not found.";
    public const string TargetParentNotFound = "Target parent habit not found.";
    public const string TagNotFound = "Tag not found.";
    public const string FactNotFound = "Fact not found.";
    public const string NoPermission = "You don't have permission to delete this habit.";
    public const string HabitNotOwned = "Habit does not belong to this user.";
    public const string GoalNotFound = "Goal not found.";
    public const string GoalNotOwned = "Goal does not belong to this user.";
    public const string ReferralNotFound = "Referral not found.";
    public const string InvalidReferralCode = "Invalid referral code.";
    public const string ReferralCapReached = "Maximum referral limit reached.";
    public const string SelfReferral = "You cannot refer yourself.";
    public const string AlreadyReferred = "This account was already referred.";
    public const string ApiKeyNotFound = "API key not found.";
    public const string NotificationNotFound = "Notification not found.";
    public const string NoPushSubscriptions = "No push subscriptions found for this user.";
    public const string SubscriptionNotFound = "No subscription found.";
    public const string InvalidBillingInterval = "Invalid billing interval.";
    public const string ChatHistoryTooLarge = "Chat history too large.";
    public const string MessageTooLong = "Message must be between 1 and 4000 characters.";
    public const string StreakFreezeNotAvailable = "No streak freeze available.";
    public const string NoStreakFreezesEarned = "You haven't earned any streak freezes yet. Keep your streak going!";
    public const string StreakFreezeMonthlyLimitReached = "You have used all 3 streak freezes this month.";
    public const string AlreadyUsedStreakFreezeToday = "Streak freeze already used today.";
    public const string NoActiveStreak = "No active streak to protect.";
    public const string NotEnoughCoins = "Not enough coins.";
    public const string SubjectRequired = "Subject is required";
    public const string MessageRequired = "Message is required";
    public const string InvalidSession = "Invalid or expired session.";
}
