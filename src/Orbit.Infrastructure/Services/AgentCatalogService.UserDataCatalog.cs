using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

#pragma warning disable S107 // Declarative catalog builders mirror the record shapes they populate.
#pragma warning disable S1192 // Catalog definitions intentionally reuse product vocabulary and JSON schema literals.
#pragma warning disable CA1861 // Static catalog schemas are evaluated once at startup and are not hot-path allocations.

public partial class AgentCatalogService
{
    private static IReadOnlyList<UserDataCatalogEntry> BuildUserDataCatalog()
    {
        return
        [
            .. ProfileHabitAndGoalDataEntries(),
            .. MemoryCalendarAndNotificationDataEntries(),
            .. GamificationReferralAndBillingDataEntries(),
            .. SupportSyncAndCredentialDataEntries()
        ];
    }

    private static UserDataCatalogEntry[] ProfileHabitAndGoalDataEntries()
    {
        return
        [
            new UserDataCatalogEntry(
                "profile",
                "Profile Identity",
                "Name, email, language, timezone, onboarding, and preference state.",
                "moderate",
                "Retained for the lifetime of the account unless the account is deleted.",
                AiReadable: true,
                AiMutableInPhaseOne: true,
                Fields:
                [
                    new UserDataFieldDescriptor("Name", "Display name for the user.", true, false),
                    new UserDataFieldDescriptor("Email", "Primary account email.", true, false),
                    new UserDataFieldDescriptor("Language", "Preferred UI language.", true, true),
                    new UserDataFieldDescriptor("TimeZone", "IANA timezone used for user-facing dates.", true, true),
                    new UserDataFieldDescriptor("ThemePreference", "Preferred light/dark theme.", true, true),
                    new UserDataFieldDescriptor("ColorScheme", "Preferred accent color scheme.", true, true)
                ]),

            new UserDataCatalogEntry(
                "habits",
                "Habits, Logs, Tags, And Checklist Templates",
                "Recurring habits, one-time tasks, logs, tags, checklist templates, and related schedule state.",
                "moderate",
                "Retained until deleted by the user; logs remain part of progress history.",
                AiReadable: true,
                AiMutableInPhaseOne: true,
                Fields:
                [
                    new UserDataFieldDescriptor("Habit.Title", "Habit or task title.", true, true),
                    new UserDataFieldDescriptor("Habit.Description", "Optional habit description.", true, true),
                    new UserDataFieldDescriptor("Habit.Schedule", "Frequency, due date, due time, reminders, and flags.", true, true),
                    new UserDataFieldDescriptor("Tag.Name", "Tag name.", true, true),
                    new UserDataFieldDescriptor("ChecklistTemplate.Items", "Reusable checklist item text.", true, true)
                ]),

            new UserDataCatalogEntry(
                "goals",
                "Goals And Progress",
                "Goal titles, descriptions, target values, status, linked habits, and progress logs.",
                "moderate",
                "Retained until deleted by the user.",
                AiReadable: true,
                AiMutableInPhaseOne: true,
                Fields:
                [
                    new UserDataFieldDescriptor("Goal.Title", "Goal title.", true, true),
                    new UserDataFieldDescriptor("Goal.Description", "Goal description.", true, true),
                    new UserDataFieldDescriptor("Goal.TargetValue", "Target numeric progress.", true, true),
                    new UserDataFieldDescriptor("Goal.Status", "Goal lifecycle state.", true, true),
                    new UserDataFieldDescriptor("GoalProgressLog.Note", "Optional progress note.", true, false)
                ])
        ];
    }

    private static UserDataCatalogEntry[] MemoryCalendarAndNotificationDataEntries()
    {
        return
        [
            new UserDataCatalogEntry(
                "user-facts",
                "AI Memory Facts",
                "Compact facts extracted from prior conversations when AI memory is enabled.",
                "high",
                "Retained until deleted or memory is cleared; facts are soft-deleted for auditability.",
                AiReadable: true,
                AiMutableInPhaseOne: true,
                Fields:
                [
                    new UserDataFieldDescriptor("FactText", "User fact remembered by AI.", true, false),
                    new UserDataFieldDescriptor("Category", "Fact category label.", true, false)
                ]),

            new UserDataCatalogEntry(
                "calendar",
                "Calendar Integration",
                "Calendar connection status, auto-sync state, and suggestion metadata.",
                "high",
                "Connection state is retained while the integration exists; suggestion data is retained until dismissed or imported.",
                AiReadable: true,
                AiMutableInPhaseOne: true,
                Fields:
                [
                    new UserDataFieldDescriptor("HasGoogleConnection", "Whether Google Calendar is connected.", true, false),
                    new UserDataFieldDescriptor("AutoSyncState", "Auto-sync enabled/status flags.", true, true),
                    new UserDataFieldDescriptor("Suggestion.Title", "Imported suggestion title.", true, true),
                    new UserDataFieldDescriptor("Suggestion.RawEventJson", "Raw Google event payload.", false, false),
                    new UserDataFieldDescriptor("GoogleAccessToken", "Encrypted Google access token.", false, false),
                    new UserDataFieldDescriptor("GoogleRefreshToken", "Encrypted Google refresh token.", false, false)
                ]),

            new UserDataCatalogEntry(
                "notifications",
                "Notifications And Push",
                "In-app notifications and push-subscription state.",
                "high",
                "Notifications are retained until deleted; push subscriptions are retained while active.",
                AiReadable: true,
                AiMutableInPhaseOne: true,
                Fields:
                [
                    new UserDataFieldDescriptor("Notification.Title", "Notification title.", true, true),
                    new UserDataFieldDescriptor("Notification.Body", "Notification body.", true, true),
                    new UserDataFieldDescriptor("Notification.Url", "Optional navigation URL.", true, true),
                    new UserDataFieldDescriptor("PushSubscription.Endpoint", "Push endpoint URL.", false, false),
                    new UserDataFieldDescriptor("PushSubscription.CryptoMaterial", "VAPID/FCM subscription keys.", false, false)
                ])
        ];
    }

    private static UserDataCatalogEntry[] GamificationReferralAndBillingDataEntries()
    {
        return
        [
            new UserDataCatalogEntry(
                "gamification",
                "Gamification",
                "Streaks, streak freezes, XP, levels, and achievement progress.",
                "moderate",
                "Retained while the account exists so long-term streak and progress history can be calculated.",
                AiReadable: true,
                AiMutableInPhaseOne: true,
                Fields:
                [
                    new UserDataFieldDescriptor("CurrentStreak", "Current streak length.", true, false),
                    new UserDataFieldDescriptor("LongestStreak", "Longest recorded streak.", true, false),
                    new UserDataFieldDescriptor("ExperiencePoints", "Accumulated XP total.", true, false),
                    new UserDataFieldDescriptor("Level", "Derived level from XP.", true, false),
                    new UserDataFieldDescriptor("AvailableStreakFreezes", "Remaining streak freezes.", true, true)
                ]),

            new UserDataCatalogEntry(
                "referrals",
                "Referrals",
                "Referral code, referral dashboard metrics, and earned reward state.",
                "moderate",
                "Retained for reward attribution and growth reporting until the account is deleted.",
                AiReadable: true,
                AiMutableInPhaseOne: false,
                Fields:
                [
                    new UserDataFieldDescriptor("ReferralCode", "User's referral code.", true, false),
                    new UserDataFieldDescriptor("ReferralLink", "Generated referral share link.", true, false),
                    new UserDataFieldDescriptor("SuccessfulReferrals", "Count of converted referrals.", true, false),
                    new UserDataFieldDescriptor("RewardStatus", "Referral reward eligibility and claim state.", true, false)
                ]),

            new UserDataCatalogEntry(
                "subscriptions",
                "Billing And Entitlements",
                "Plan status, billing details, trial state, and purchase-linked metadata.",
                "critical",
                "Retained for account lifecycle and financial audit requirements.",
                AiReadable: true,
                AiMutableInPhaseOne: false,
                Fields:
                [
                    new UserDataFieldDescriptor("Plan", "Current plan and entitlement state.", true, false),
                    new UserDataFieldDescriptor("TrialEndsAt", "Trial expiration timestamp.", true, false),
                    new UserDataFieldDescriptor("BillingPortalStatus", "Whether billing management is available.", true, false),
                    new UserDataFieldDescriptor("StripeCustomerId", "Internal Stripe customer identifier.", false, false),
                    new UserDataFieldDescriptor("StripeSubscriptionId", "Internal Stripe subscription identifier.", false, false)
                ])
        ];
    }

    private static UserDataCatalogEntry[] SupportSyncAndCredentialDataEntries()
    {
        return
        [
            new UserDataCatalogEntry(
                "support",
                "Support Requests",
                "Submitted support contact information and messages.",
                "high",
                "Retained according to operational support retention requirements.",
                AiReadable: true,
                AiMutableInPhaseOne: true,
                Fields:
                [
                    new UserDataFieldDescriptor("SupportRequest.Name", "Support contact name.", true, true),
                    new UserDataFieldDescriptor("SupportRequest.Email", "Support contact email.", true, true),
                    new UserDataFieldDescriptor("SupportRequest.Subject", "Support request subject.", true, true),
                    new UserDataFieldDescriptor("SupportRequest.Message", "Support request body.", true, true)
                ]),

            new UserDataCatalogEntry(
                "sync",
                "Offline Sync State",
                "Versioned sync exports, deleted references, and client replay state for first-party apps.",
                "moderate",
                "Retained while sync clients are active and according to cleanup policies for offline state.",
                AiReadable: true,
                AiMutableInPhaseOne: false,
                Fields:
                [
                    new UserDataFieldDescriptor("SyncHabitDto", "Redacted sync export for habits.", true, false),
                    new UserDataFieldDescriptor("SyncGoalDto", "Redacted sync export for goals.", true, false),
                    new UserDataFieldDescriptor("SyncDeletedRef", "Deleted entity reference for client reconciliation.", true, false),
                    new UserDataFieldDescriptor("RawEntitySerialization", "Legacy raw sync payload.", false, false)
                ]),

            new UserDataCatalogEntry(
                "auth-and-api",
                "Sessions, Auth, And API Keys",
                "Session tokens, OAuth codes, and API-key metadata.",
                "critical",
                "Retained according to security and session lifecycle requirements.",
                AiReadable: false,
                AiMutableInPhaseOne: false,
                Fields:
                [
                    new UserDataFieldDescriptor("UserSession.TokenHash", "Hashed refresh/session token.", false, false),
                    new UserDataFieldDescriptor("ApiKey.KeyHash", "Hashed API key value.", false, false),
                    new UserDataFieldDescriptor("ApiKey.Scopes", "Granted API-key scopes.", true, false),
                    new UserDataFieldDescriptor("ApiKey.ExpiresAtUtc", "API-key expiry timestamp.", true, false)
                ])
        ];
    }
}

#pragma warning restore CA1861
#pragma warning restore S1192
#pragma warning restore S107
