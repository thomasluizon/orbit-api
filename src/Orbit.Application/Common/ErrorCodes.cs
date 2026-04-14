using Orbit.Domain.Common;

namespace Orbit.Application.Common;

public static class ErrorCodes
{
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string HabitNotFound = "HABIT_NOT_FOUND";
    public const string ParentHabitNotFound = "PARENT_HABIT_NOT_FOUND";
    public const string TargetParentNotFound = "TARGET_PARENT_NOT_FOUND";
    public const string TagNotFound = "TAG_NOT_FOUND";
    public const string FactNotFound = "FACT_NOT_FOUND";
    public const string NoPermission = "NO_PERMISSION";
    public const string HabitNotOwned = "HABIT_NOT_OWNED";
    public const string GoalNotFound = "GOAL_NOT_FOUND";
    public const string GoalNotOwned = "GOAL_NOT_OWNED";
    public const string ReferralNotFound = "REFERRAL_NOT_FOUND";
    public const string InvalidReferralCode = "INVALID_REFERRAL_CODE";
    public const string ReferralCapReached = "REFERRAL_CAP_REACHED";
    public const string SelfReferral = "SELF_REFERRAL";
    public const string AlreadyReferred = "ALREADY_REFERRED";
    public const string ApiKeyNotFound = "API_KEY_NOT_FOUND";
    public const string NotificationNotFound = "NOTIFICATION_NOT_FOUND";
    public const string NoPushSubscriptions = "NO_PUSH_SUBSCRIPTIONS";
    public const string SubscriptionNotFound = "SUBSCRIPTION_NOT_FOUND";
    public const string InvalidBillingInterval = "INVALID_BILLING_INTERVAL";
    public const string ChatHistoryTooLarge = "CHAT_HISTORY_TOO_LARGE";
    public const string MessageTooLong = "MESSAGE_TOO_LONG";
    public const string StreakFreezeNotAvailable = "STREAK_FREEZE_NOT_AVAILABLE";
    public const string NoStreakFreezesEarned = "NO_STREAK_FREEZES_EARNED";
    public const string StreakFreezeMonthlyLimitReached = "STREAK_FREEZE_MONTHLY_LIMIT_REACHED";
    public const string AlreadyUsedStreakFreezeToday = "ALREADY_USED_STREAK_FREEZE_TODAY";
    public const string NoActiveStreak = "NO_ACTIVE_STREAK";
    public const string NotEnoughCoins = "NOT_ENOUGH_COINS";
    public const string AlreadyLogged = "ALREADY_LOGGED";
    public const string MaxSubHabitsReached = "MAX_SUB_HABITS_REACHED";
    public const string MaxDepthReached = "MAX_DEPTH_REACHED";
    public const string CircularReference = "CIRCULAR_REFERENCE";
    public const string InvalidVerificationCode = "INVALID_VERIFICATION_CODE";
    public const string CodeExpired = "CODE_EXPIRED";
    public const string TooManyAttempts = "TOO_MANY_ATTEMPTS";
    public const string InvalidGoogleToken = "INVALID_GOOGLE_TOKEN";
    public const string PayGate = Result.PayGateErrorCode;
    public const string InvalidSession = "INVALID_SESSION";
}
