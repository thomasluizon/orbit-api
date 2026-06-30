using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

#pragma warning disable S107 // Declarative catalog builders mirror the record shapes they populate.
#pragma warning disable S1192 // Catalog definitions intentionally reuse product vocabulary and JSON schema literals.
#pragma warning disable CA1861 // Static catalog schemas are evaluated once at startup and are not hot-path allocations.

public partial class AgentCatalogService
{
    private static IReadOnlyList<AppSurface> BuildSurfaces()
    {
        return
        [
            .. DailyUseSurfaces(),
            .. PreferenceAndNotificationSurfaces(),
            .. GoalApiAndOnboardingSurfaces(),
            .. EngagementAndBillingSurfaces(),
            .. SupportAccountAndSyncSurfaces()
        ];
    }

    private static AppSurface[] DailyUseSurfaces()
    {
        return
        [
            new AppSurface(
                "today",
                "Today",
                "Shows the current day's scheduled habits, AI summary, and progress status.",
                ["Open Today.", "Review scheduled habits and overdue items.", "Log, skip, or inspect the day's tasks."],
                ["Uses timezone-aware dates from the backend.", "General habits can be hidden locally on some clients."],
                [AgentCapabilityIds.HabitsRead, AgentCapabilityIds.HabitsWrite, AgentCapabilityIds.DailySummaryRead],
                ["HabitsController.GetHabits", "HabitsController.GetDailySummary"]),

            new AppSurface(
                "calendar-overview",
                "Calendar",
                "Shows imported Google Calendar events and calendar sync suggestions.",
                ["Open Calendar.", "Review upcoming events.", "Inspect sync suggestions before importing or dismissing them."],
                ["Calendar reads are safe.", "Raw Google event payloads are never exposed to AI."],
                [AgentCapabilityIds.CalendarRead],
                ["CalendarController.GetEvents", "CalendarController.GetSuggestions"]),

            new AppSurface(
                "calendar-sync",
                "Calendar Sync",
                "Controls Google Calendar connection state, auto-sync, and suggestion reconciliation.",
                ["Open Calendar settings.", "Review Google connection status.", "Enable, disable, or reconcile sync when needed."],
                ["Auto-sync state depends on Google connection and Pro access.", "Suggestion dismissal and import mutations are destructive."],
                [AgentCapabilityIds.CalendarRead, AgentCapabilityIds.CalendarSyncManage],
                ["CalendarController.GetAutoSyncState", "CalendarController.SetAutoSync"]),

            new AppSurface(
                "chat",
                "AI Chat",
                "Lets the assistant explain the app and execute safe Orbit operations.",
                ["Send a prompt to the chat endpoint.", "The backend resolves tool calls.", "Review pending confirmations before destructive actions."],
                ["Chat may use clientContext as UI hints only.", "Authorization is always backend-enforced."],
                [AgentCapabilityIds.ChatInteract],
                ["ChatController.ProcessChat", "ChatController.ProcessChatStream", "ChatController.Transcribe"])
        ];
    }

    private static AppSurface[] PreferenceAndNotificationSurfaces()
    {
        return
        [
            new AppSurface(
                "profile-preferences",
                "Profile And Preferences",
                "Stores identity, timezone, language, theme, color scheme, and week-start preferences.",
                ["Open Profile.", "Review identity, plan, and settings.", "Update timezone, language, theme, or week-start preferences."],
                ["Theme and color are user preferences, not authorization signals.", "Preference changes are low-risk writes."],
                [AgentCapabilityIds.ProfileReadBasic, AgentCapabilityIds.ProfilePreferencesWrite, AgentCapabilityIds.ProfilePremiumAppearanceWrite],
                ["ProfileController.GetProfile", "ProfileController.SetTimezone"]),

            new AppSurface(
                "ai-settings",
                "AI Settings",
                "Controls AI memory, daily AI summary, and proactive Astra check-in behavior.",
                ["Open Profile AI settings.", "Review AI memory, summary, and proactive check-in toggles.", "Enable or disable the AI features you want."],
                ["AI memory controls whether compact facts may be stored.", "Settings are enforced on the backend, not by prompt text."],
                [AgentCapabilityIds.ProfileReadBasic, AgentCapabilityIds.ProfileAiMemoryWrite, AgentCapabilityIds.ProfileAiSummaryWrite, AgentCapabilityIds.ProfileProactiveAstraWrite, AgentCapabilityIds.UserFactsRead, AgentCapabilityIds.UserFactsDelete],
                ["ProfileController.SetAiMemory", "ProfileController.SetAiSummary", "ProfileController.SetProactiveAstra"]),

            new AppSurface(
                "notifications",
                "Notifications",
                "Stores in-app reminders, read state, and push subscription status.",
                ["Open Notifications.", "Mark items read or manage subscriptions.", "Delete individual items only after confirmation."],
                ["Push subscription cryptographic material is never AI-readable.", "Deletion requires confirmation."],
                [AgentCapabilityIds.NotificationsRead, AgentCapabilityIds.NotificationsWrite, AgentCapabilityIds.NotificationsDelete],
                ["NotificationController.GetNotifications", "NotificationController.Subscribe"])
        ];
    }

    private static AppSurface[] GoalApiAndOnboardingSurfaces()
    {
        return
        [
            new AppSurface(
                "goals",
                "Goals",
                "Tracks goals, progress, metrics, and linked habits.",
                ["Open Goals.", "Review progress or metrics.", "Create, update, or link habits to a goal."],
                ["Goal deletion is destructive.", "Goal reviews are AI-generated read operations."],
                [AgentCapabilityIds.GoalsRead, AgentCapabilityIds.GoalsWrite, AgentCapabilityIds.GoalsDelete],
                ["GoalsController.GetGoals", "GoalsController.CreateGoal"]),

            new AppSurface(
                "advanced-api",
                "Advanced And API",
                "Holds API keys, MCP integration, and metadata endpoints for AI clients.",
                ["Open Advanced/API.", "Review scopes and metadata.", "Create or revoke keys only with human review."],
                ["API-key creation and revocation require fresh confirmation plus step-up authorization.", "MCP tool calls use the same policy catalog as chat."],
                [AgentCapabilityIds.ApiKeysRead, AgentCapabilityIds.ApiKeysManage, AgentCapabilityIds.CatalogCapabilitiesRead],
                ["ApiKeysController.GetApiKeys", "AiController.GetCapabilitiesMetadata"]),

            new AppSurface(
                "onboarding-auth",
                "Onboarding And Auth",
                "Handles onboarding completion, direct sign-in flows, refresh, logout, and OAuth exchange.",
                ["Request a sign-in code or OAuth flow.", "Verify identity and exchange tokens.", "Complete onboarding and tour flows when the client is ready."],
                ["Redirect URIs must pass the allowlist on every OAuth step.", "Direct auth operations are typed but require a direct client flow, not an agent mutation."],
                [AgentCapabilityIds.AuthManage, AgentCapabilityIds.ProfilePreferencesWrite],
                ["AuthController.SendCode", "OAuthController.Token"])
        ];
    }

    private static AppSurface[] EngagementAndBillingSurfaces()
    {
        return
        [
            new AppSurface(
                "gamification",
                "Gamification",
                "Shows streaks, freezes, XP, levels, and achievements.",
                ["Open streak profile or achievements.", "Review freeze availability.", "Activate a freeze when needed."],
                ["Freeze activation is a mutation.", "Level and XP are derived state."],
                [AgentCapabilityIds.GamificationRead],
                ["GamificationController.GetProfile"]),

            new AppSurface(
                "referrals",
                "Referrals",
                "Shows the referral dashboard, code, link, and reward stats.",
                ["Open the referral dashboard.", "Review code, link, and earned rewards.", "Share the referral link from the client."],
                ["Referral reads are safe.", "Reward redemption is governed by subscription and billing state."],
                [AgentCapabilityIds.ReferralsRead],
                ["ReferralController.GetDashboard", "ReferralController.GetStats"]),

            new AppSurface(
                "subscriptions",
                "Billing And Subscriptions",
                "Shows plans, billing details, checkout flows, and ad reward actions.",
                ["Read plan and billing state.", "Open checkout or portal only after explicit confirmation and step-up authorization.", "Claim ad rewards from the subscription surface when eligible."],
                ["Billing mutations require step-up authorization.", "Internal Stripe identifiers are never exposed to AI."],
                [AgentCapabilityIds.SubscriptionsRead, AgentCapabilityIds.SubscriptionsManage],
                ["SubscriptionController.GetStatus", "SubscriptionController.CreateCheckout"])
        ];
    }

    private static AppSurface[] SupportAccountAndSyncSurfaces()
    {
        return
        [
            new AppSurface(
                "support",
                "Support",
                "Lets the user send a support request with contact details and a message.",
                ["Open Support.", "Fill in name, email, subject, and message.", "Submit the request."],
                ["Support requests are low-risk writes.", "Support mail content is user-provided and auditable."],
                [AgentCapabilityIds.SupportWrite],
                ["SupportController.SendSupport"]),

            new AppSurface(
                "account-lifecycle",
                "Account Lifecycle",
                "Handles account reset, deletion request, and deletion confirmation flows.",
                ["Review the account impact.", "Request deletion or reset only after human review.", "Confirm with backend-issued confirmation and step-up when required."],
                ["High-risk account lifecycle writes require explicit human confirmation.", "Deactivated accounts remain locked down except for auth and deletion flows."],
                [AgentCapabilityIds.AccountManage],
                ["AuthController.RequestDeletion", "ProfileController.ResetAccount"]),

            new AppSurface(
                "sync",
                "Offline Sync",
                "Provides versioned incremental sync for first-party clients.",
                ["Call sync changes with a recent timestamp.", "Process redacted DTOs.", "Send mutations in batch for server-wins resolution."],
                ["Version 2 payloads are curated and redacted.", "The legacy raw sync payload remains for compatibility during migration."],
                [AgentCapabilityIds.SyncRead, AgentCapabilityIds.SyncWrite],
                ["SyncController.GetChangesV2", "SyncController.ProcessBatch"])
        ];
    }
}

#pragma warning restore CA1861
#pragma warning restore S1192
#pragma warning restore S107
