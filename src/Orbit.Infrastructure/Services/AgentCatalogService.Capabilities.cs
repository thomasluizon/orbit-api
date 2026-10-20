using Orbit.Application.FeatureFlags;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Services;

public partial class AgentCatalogService
{
    private const string CatalogDomain = "catalog";
    private const string HabitsDomain = "habits";
    private const string GoalsDomain = "goals";
    private const string ProfileDomain = "profile";
    private const string NotificationsDomain = "notifications";
    private const string GamificationDomain = "gamification";
    private const string RecordListPageAction = "ChatController.GetRecordListPage";

    private static IReadOnlyList<AgentCapability> BuildCapabilities()
    {
        return
        [
            .. ChatCapabilities(),
            .. CatalogCapabilities(),
            .. ConfigCapabilities(),
            new AgentCapability(FeatureFlagActions.CapabilityId,
                "Manage Feature Flags", "Administers feature flags and account targeting.", "admin",
                FeatureFlagActions.Scope, AgentRiskClass.Low,
                IsMutation: true, IsPhaseOneReadOnly: false, AgentConfirmationRequirement.None,
                ChatToolNames: [FeatureFlagActions.ToolName],
                McpToolNames: [FeatureFlagActions.ToolName],
                ControllerActionKeys: ["AdminController.ListFeatureFlags", "AdminController.ManageFeatureFlags"]),
            .. HabitCoreCapabilities(),
            .. HabitBulkAndInsightCapabilities(),
            .. GoalCapabilities(),
            .. TagCapabilities(),
            .. ProfileCapabilities(),
            .. ProactiveCapabilities(),
            .. NotificationCapabilities(),
            .. CalendarCapabilities(),
            .. NotionCapabilities(),
            .. GamificationCapabilities(),
            .. ChecklistTemplateCapabilities(),
            .. ReferralCapabilities(),
            .. SubscriptionCapabilities(),
            .. ApiKeyCapabilities(),
            .. SupportCapabilities(),
            .. SyncCapabilities(),
            .. AccountAndAuthCapabilities(),
        ];
    }

    private static AgentCapability[] ChatCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.ChatInteract,
                "Chat Interaction",
                "Accepts a user chat request and coordinates safe Orbit operations.",
                "chat",
                AgentScopes.ChatInteract,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                McpToolNames: ["execute_agent_operation_v2", "confirm_agent_operation_v2", "step_up_agent_operation_v2", "verify_step_up_agent_operation_v2"],
                ControllerActionKeys:
                [
                    "ChatController.ProcessChat",
                    "ChatController.ProcessChatStream",
                    "ChatController.Transcribe",
                    "AiController.ConfirmPendingOperation",
                    "AiController.RefreshPendingOperationPreview",
                    "AiController.RevisePendingOperation",
                    "AiController.MarkPendingOperationStepUp",
                    "AiController.VerifyPendingOperationStepUp",
                    "AiController.ExecutePendingOperation",
                    "AiController.ResolveClarification"
                ])
        ];
    }

    private static AgentCapability[] CatalogCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.CatalogCapabilitiesRead,
                "Read Capability Catalog",
                "Lists the Orbit capability catalog available to AI clients.",
                CatalogDomain,
                AgentScopes.CatalogRead,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                McpToolNames: ["list_agent_capabilities_v2", "list_agent_operations_v2"],
                ControllerActionKeys: ["AiController.GetCapabilitiesMetadata", "AiController.GetOperationsMetadata"]),

            new AgentCapability(
                AgentCapabilityIds.CatalogDataRead,
                "Read User Data Catalog",
                "Lists the catalog of user-data classes, sensitivity, and AI mutability.",
                CatalogDomain,
                AgentScopes.CatalogRead,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                McpToolNames: ["list_user_data_catalog_v2"],
                ControllerActionKeys: ["AiController.GetUserDataCatalog"]),

            new AgentCapability(
                AgentCapabilityIds.CatalogSurfacesRead,
                "Read App Surface Catalog",
                "Lists Orbit product surfaces and how-to guidance.",
                CatalogDomain,
                AgentScopes.CatalogRead,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                McpToolNames: ["list_app_surfaces_v2"],
                ControllerActionKeys: ["AiController.GetAppSurfaces"]),

            new AgentCapability(
                AgentCapabilityIds.DescribeFeature,
                "Describe Feature",
                "Returns an authoritative explanation of an Orbit feature's mechanics.",
                CatalogDomain,
                AgentScopes.CatalogRead,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["describe_feature"],
                McpToolNames: ["describe_feature"])
        ];
    }

    private static AgentCapability[] ConfigCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.ConfigRead,
                "Read App Config",
                "Reads frontend-visible configuration needed by Orbit clients.",
                "config",
                AgentScopes.ReadConfig,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ControllerActionKeys: ["ConfigController.GetConfig"])
        ];
    }

    private static AgentCapability[] HabitCoreCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.HabitsRead,
                "Read Habits",
                "Reads habits, logs, and schedule state.",
                HabitsDomain,
                AgentScopes.ReadHabits,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["query_habits"],
                McpToolNames: ["list_habits", "get_habit", "get_habit_logs", "get_all_habit_logs"],
                ControllerActionKeys:
                [
                    "HabitsController.GetHabits",
                    "HabitsController.GetHabitCount",
                    "HabitsController.GetHabitWidget",
                    "HabitsController.GetCalendarMonth",
                    "HabitsController.GetHabitById",
                    "HabitsController.GetHabitDetail",
                    "HabitsController.GetRescheduleSuggestion",
                    "HabitsController.GetLogs",
                    "HabitsController.GetTrends"
                ]),

            new AgentCapability(
                AgentCapabilityIds.HabitsWrite,
                "Write Habits",
                "Creates and updates habits, checklists, and hierarchy links.",
                HabitsDomain,
                AgentScopes.WriteHabits,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["create_habit", "update_habit", "bulk_update_habit_emojis", "create_sub_habit", "duplicate_habit", "move_habit", "move_habit_parent", "log_habit", "unlog_habit", "skip_habit", "undo_skip_habit", "suggest_breakdown", "update_checklist", "reorder_habits", "link_goals_to_habit"],
                McpToolNames:
                [
                    "create_habit",
                    "update_habit",
                    "bulk_update_habit_emojis",
                    "log_habit",
                    "unlog_habit",
                    "skip_habit", "undo_skip_habit",
                    "update_checklist",
                    "create_sub_habit",
                    "duplicate_habit",
                    "reorder_habits",
                    "move_habit_parent",
                    "link_goals_to_habit"
                ],
                ControllerActionKeys:
                [
                    "HabitsController.CreateHabit",
                    "HabitsController.SuggestSetup",
                    "HabitsController.LogHabit",
                    "HabitsController.SkipHabit",
                    "HabitsController.UndoSkipHabit",
                    "HabitsController.UpdateHabit",
                    "HabitsController.UpdateChecklist",
                    "HabitsController.ReorderHabits",
                    "HabitsController.MoveHabitParent",
                    "HabitsController.DuplicateHabit",
                    "HabitsController.CreateSubHabit",
                    "HabitsController.LinkGoals",
                    "HabitsController.RestoreHabit"
                ]),

            new AgentCapability(
                AgentCapabilityIds.HabitsDelete,
                "Delete Habits",
                "Deletes individual habits.",
                HabitsDomain,
                AgentScopes.DeleteHabits,
                AgentRiskClass.Destructive,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                ChatToolNames: ["delete_habit"],
                McpToolNames: ["delete_habit"],
                ControllerActionKeys: ["HabitsController.DeleteHabit"])
        ];
    }

    private static AgentCapability[] HabitBulkAndInsightCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.HabitsBulkWrite,
                "Bulk Habit Updates",
                "Runs reversible bulk create, log, skip, update, and reschedule habit operations.",
                HabitsDomain,
                AgentScopes.WriteHabits,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["bulk_update_habits", "bulk_reschedule_habits", "bulk_log_habits", "bulk_skip_habits", "bulk_create_habits"],
                McpToolNames: ["bulk_update_habits", "bulk_reschedule_habits", "bulk_create_habits", "bulk_log_habits", "bulk_skip_habits"],
                ControllerActionKeys: ["HabitsController.BulkCreate", "HabitsController.BulkLog", "HabitsController.BulkSkip"]),

            new AgentCapability(
                AgentCapabilityIds.HabitsBulkDelete,
                "Bulk Habit Delete",
                "Deletes multiple habits in one operation.",
                HabitsDomain,
                AgentScopes.DeleteHabits,
                AgentRiskClass.Destructive,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                ChatToolNames: ["bulk_delete_habits"],
                McpToolNames: ["bulk_delete_habits"],
                ControllerActionKeys: ["HabitsController.BulkDelete"]),

            new AgentCapability(
                AgentCapabilityIds.HabitMetricsRead,
                "Read Habit Metrics",
                "Reads non-premium habit metrics.",
                HabitsDomain,
                AgentScopes.ReadHabits,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["get_habit_metrics"],
                McpToolNames: ["get_habit_metrics"],
                ControllerActionKeys: ["HabitsController.GetMetrics"]),

            new AgentCapability(
                AgentCapabilityIds.DailySummaryRead,
                "Read Daily Summary",
                "Reads the premium AI daily summary.",
                HabitsDomain,
                AgentScopes.ReadHabits,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                FeatureFlagKeys: ["ai_summary"],
                ChatToolNames: ["get_daily_summary"],
                McpToolNames: ["get_daily_summary"],
                ControllerActionKeys: ["HabitsController.GetDailySummary"]),

            new AgentCapability(
                AgentCapabilityIds.RetrospectiveRead,
                "Read Retrospective",
                "Reads the Pro retrospective.",
                HabitsDomain,
                AgentScopes.ReadHabits,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                FeatureFlagKeys: ["ai_retrospective"],
                ChatToolNames: ["get_retrospective"],
                McpToolNames: ["get_retrospective"],
                ControllerActionKeys: ["HabitsController.GetRetrospective"])
        ];
    }

    private static AgentCapability[] GoalCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.GoalsRead,
                "Read Goals",
                "Reads goals, reviews, metrics, and linked habit progress.",
                GoalsDomain,
                AgentScopes.ReadGoals,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["query_goals", "review_goals"],
                McpToolNames: ["list_goals", "get_goal", "get_goal_metrics", "get_goal_review"],
                ControllerActionKeys:
                [
                    "GoalsController.GetGoals",
                    "GoalsController.GetGoalById",
                    "GoalsController.GetGoalDetail",
                    "GoalsController.GetGoalMetrics",
                    "GoalsController.GetGoalReview",
                    "GoalsController.GetGoalProgressHistory"
                ]),

            new AgentCapability(
                AgentCapabilityIds.GoalsWrite,
                "Write Goals",
                "Creates and updates goals, progress, ordering, and habit links.",
                GoalsDomain,
                AgentScopes.WriteGoals,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["create_goal", "update_goal", "update_goal_status", "update_goal_progress", "link_habits_to_goal", "reorder_goals"],
                McpToolNames: ["create_goal", "update_goal", "update_goal_progress", "update_goal_status", "reorder_goals", "link_habits_to_goal"],
                ControllerActionKeys:
                [
                    "GoalsController.CreateGoal",
                    "GoalsController.UpdateGoal",
                    "GoalsController.UpdateProgress",
                    "GoalsController.UpdateStatus",
                    "GoalsController.ReorderGoals",
                    "GoalsController.LinkHabits",
                    "GoalsController.RestoreGoal"
                ]),

            new AgentCapability(
                AgentCapabilityIds.GoalsDelete,
                "Delete Goals",
                "Deletes goals.",
                GoalsDomain,
                AgentScopes.DeleteGoals,
                AgentRiskClass.Destructive,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                ChatToolNames: ["delete_goal"],
                McpToolNames: ["delete_goal"],
                ControllerActionKeys: ["GoalsController.DeleteGoal"])
        ];
    }

    private static AgentCapability[] TagCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.TagsRead,
                "Read Tags",
                "Reads user tags and habit tag assignments.",
                "tags",
                AgentScopes.ReadTags,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["list_tags"],
                McpToolNames: ["list_tags", "get_tag_page"],
                ControllerActionKeys: ["TagsController.GetTags", RecordListPageAction]),

            new AgentCapability(
                AgentCapabilityIds.TagsWrite,
                "Write Tags",
                "Creates, updates, and assigns tags.",
                "tags",
                AgentScopes.WriteTags,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["assign_tags", "create_tag", "update_tag"],
                McpToolNames: ["create_tag", "update_tag", "assign_tags"],
                ControllerActionKeys: ["TagsController.CreateTag", "TagsController.UpdateTag", "TagsController.AssignTags", "TagsController.SuggestTags", "TagsController.RestoreTag"]),

            new AgentCapability(
                AgentCapabilityIds.TagsDelete,
                "Delete Tags",
                "Deletes tags.",
                "tags",
                AgentScopes.DeleteTags,
                AgentRiskClass.Destructive,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                ChatToolNames: ["delete_tag"],
                McpToolNames: ["delete_tag"],
                ControllerActionKeys: ["TagsController.DeleteTag"])
        ];
    }

    private static AgentCapability[] ProfileCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.ProfileReadBasic,
                "Read Profile",
                "Reads user profile and preference state without exposing secrets.",
                ProfileDomain,
                AgentScopes.ReadProfileBasic,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["get_profile"],
                McpToolNames: ["get_profile"],
                ControllerActionKeys: ["ProfileController.GetProfile"]),

            new AgentCapability(
                AgentCapabilityIds.ProfilePreferencesWrite,
                "Write Preferences",
                "Writes display name, timezone, language, clock format, light or dark mode, onboarding, and week-start preferences. Orbit uses one accent.",
                ProfileDomain,
                AgentScopes.WriteProfilePreferences,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["update_profile_preferences"],
                McpToolNames: ["set_name", "set_timezone", "set_language", "set_week_start_day", "set_clock_format"],
                ControllerActionKeys:
                [
                    "ProfileController.SetName",
                    "ProfileController.SetTimezone",
                    "ProfileController.SetLanguage",
                    "ProfileController.SetWeekStartDay",
                    "ProfileController.SetClockFormat",
                    "ProfileController.SetThemePreference",
                    "ProfileController.CompleteOnboarding",
                    "ProfileController.ApplyOnboarding",
                    "ProfileController.DismissImportPrompt",
                    "ProfileController.CompleteTour",
                    "ProfileController.ResetTour",
                    "ProfileController.UpdateMarketingConsent"
                ]),

            new AgentCapability(
                AgentCapabilityIds.ProfilePremiumAppearanceWrite,
                "Legacy Color Scheme Write",
                "Accepts legacy HTTP color scheme writes. Orbit uses one accent and offers light or dark mode.",
                ProfileDomain,
                AgentScopes.WriteProfilePreferences,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ControllerActionKeys: ["ProfileController.SetColorScheme"]),

            new AgentCapability(
                AgentCapabilityIds.ProfileAiSummaryWrite,
                "Write AI Summary Settings",
                "Updates AI summary preferences.",
                ProfileDomain,
                AgentScopes.WriteAiSettings,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                FeatureFlagKeys: ["ai_summary"],
                ChatToolNames: ["set_ai_summary"],
                McpToolNames: ["set_ai_summary"],
                ControllerActionKeys: ["ProfileController.SetAiSummary"]),

            new AgentCapability(
                AgentCapabilityIds.ProfileProactiveAstraWrite,
                "Write Proactive Astra Settings",
                "Stores an explicit preference for proactive Astra pushes; in-app items remain available.",
                ProfileDomain,
                AgentScopes.WriteAiSettings,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                ChatToolNames: ["set_proactive_astra"],
                McpToolNames: ["set_proactive_astra"],
                ControllerActionKeys: ["ProfileController.SetProactiveAstra"])
        ];
    }

    private static AgentCapability[] ProactiveCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.ProactiveItemsRead,
                "Read Proactive Astra Item",
                "Returns the current item and records its first display, or reads an owned item's lifecycle status without recording outcomes.",
                "proactive",
                AgentScopes.ReadNotifications,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                ControllerActionKeys: ["ProactiveAstraController.Current", "ProactiveAstraController.GetById"]),

            new AgentCapability(
                AgentCapabilityIds.ProactiveItemsWrite,
                "Record Proactive Astra Outcome",
                "Records the first opening or action for a proactive item owned by the person.",
                "proactive",
                AgentScopes.WriteNotifications,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                ControllerActionKeys: ["ProactiveAstraController.Opened", "ProactiveAstraController.Acted"])
        ];
    }

    private static AgentCapability[] NotificationCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.NotificationsRead,
                "Read Notifications",
                "Reads in-app notifications.",
                NotificationsDomain,
                AgentScopes.ReadNotifications,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["get_notifications"],
                McpToolNames: ["get_notifications", "get_notification_page"],
                ControllerActionKeys: ["NotificationController.GetNotifications", "NotificationController.GetSubscriptions", RecordListPageAction]),

            new AgentCapability(
                AgentCapabilityIds.NotificationsWrite,
                "Write Notifications",
                "Marks notifications as read and manages push subscriptions.",
                NotificationsDomain,
                AgentScopes.WriteNotifications,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["update_notifications"],
                McpToolNames: ["mark_notification_read", "mark_all_notifications_read", "subscribe_push", "unsubscribe_push", "test_push"],
                ControllerActionKeys:
                [
                    "NotificationController.MarkAsRead",
                    "NotificationController.MarkAllAsRead",
                    "NotificationController.Subscribe",
                    "NotificationController.Unsubscribe",
                    "NotificationController.TestPush"
                ]),

            new AgentCapability(
                AgentCapabilityIds.NotificationsDelete,
                "Delete Notifications",
                "Deletes one or all notifications.",
                NotificationsDomain,
                AgentScopes.DeleteNotifications,
                AgentRiskClass.Destructive,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                ChatToolNames: ["delete_notifications"],
                McpToolNames: ["delete_notification"],
                ControllerActionKeys: ["NotificationController.Delete", "NotificationController.DeleteAll"])
        ];
    }

    private static AgentCapability[] CalendarCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.CalendarRead,
                "Read Calendar",
                "Reads imported events and calendar sync suggestions.",
                "calendar",
                AgentScopes.ReadCalendar,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                FeatureFlagKeys: ["calendar_integration"],
                ChatToolNames: ["get_calendar_overview"],
                McpToolNames: ["get_calendar_events", "get_calendar_overview"],
                ControllerActionKeys:
                [
                    "CalendarController.GetEvents",
                    "CalendarController.GetSuggestions",
                    "CalendarController.GetCalendars"
                ]),

            new AgentCapability(
                AgentCapabilityIds.CalendarSyncManage,
                "Manage Calendar Sync",
                "Dismisses suggestions and manages Google Calendar sync state.",
                "calendar",
                AgentScopes.ManageCalendarSync,
                AgentRiskClass.Destructive,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                FeatureFlagKeys: ["calendar_integration"],
                ChatToolNames: ["manage_calendar_sync"],
                McpToolNames: ["manage_calendar_sync", "set_selected_calendars"],
                ControllerActionKeys:
                [
                    "CalendarController.GetAutoSyncState",
                    "CalendarController.DismissImport",
                    "CalendarController.SetAutoSync",
                    "CalendarController.DismissSuggestion",
                    "CalendarController.RunSyncNow",
                    "CalendarController.SetSelectedCalendars"
                ])
        ];
    }

    private static AgentCapability[] NotionCapabilities() =>
    [
        new AgentCapability(AgentCapabilityIds.NotionRead, "Read Notion Tasks", "Reads stored shared Notion tasks.",
            "notion", AgentScopes.ReadNotion, AgentRiskClass.Low, false, false, AgentConfirmationRequirement.None,
            PlanRequirement: "Pro", FeatureFlagKeys: ["notion_integration"], ChatToolNames: ["get_notion_tasks"],
            McpToolNames: ["get_notion_tasks"],
            ControllerActionKeys: ["ConnectionsController.GetNotion"]),
        new AgentCapability(AgentCapabilityIds.NotionManage, "Manage Notion Connection", "Connects or disconnects Notion through the person's OAuth flow.",
            "notion", AgentScopes.ManageNotion, AgentRiskClass.Destructive, true, false, AgentConfirmationRequirement.None,
            PlanRequirement: "Pro", FeatureFlagKeys: ["notion_integration"],
            ControllerActionKeys: ["ConnectionsController.AuthorizeNotion", "ConnectionsController.CompleteNotion", "ConnectionsController.DisconnectNotion"])
    ];

    private static AgentCapability[] GamificationCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.GamificationRead,
                "Read Gamification",
                "Reads streaks, levels, achievements, and referral rewards context.",
                GamificationDomain,
                AgentScopes.ReadGamification,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                ChatToolNames: ["get_gamification_overview"],
                McpToolNames: ["get_gamification_profile", "get_achievements", "get_streak_info"],
                ControllerActionKeys:
                [
                    "GamificationController.GetProfile",
                    "GamificationController.GetAchievements",
                    "GamificationController.GetStreakInfo",
                    "GamificationController.GetRecap",
                    "GamificationController.GetStreakHistory",
                    "GamificationController.GetXpHistory",
                    "AchievementsController.ReportEvent"
                ]),

            new AgentCapability(
                AgentCapabilityIds.GamificationRepair,
                "Repair Streak",
                "Spends one banked freeze to repair the eligible missed streak day.",
                GamificationDomain,
                AgentScopes.WriteGamification,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                ControllerActionKeys: ["GamificationController.RepairStreak"]),

            new AgentCapability(
                AgentCapabilityIds.GamificationRepairGap,
                "Repair Streak Gap",
                "Spends banked freezes to repair an entire contiguous streak gap atomically.",
                GamificationDomain,
                AgentScopes.WriteGamification,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                ControllerActionKeys: ["StreakGapController.RepairGap"])
        ];
    }

    private static AgentCapability[] ChecklistTemplateCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.ChecklistTemplatesRead,
                "Read Checklist Templates",
                "Reads reusable checklist templates.",
                "checklists",
                AgentScopes.ReadChecklistTemplates,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                FeatureFlagKeys: ["checklist_templates"],
                ChatToolNames: ["get_checklist_templates"],
                McpToolNames: ["get_checklist_templates", "get_template_page"],
                ControllerActionKeys: ["ChecklistTemplatesController.GetTemplates", RecordListPageAction]),

            new AgentCapability(
                AgentCapabilityIds.ChecklistTemplatesWrite,
                "Write Checklist Templates",
                "Creates, updates, and deletes checklist templates.",
                "checklists",
                AgentScopes.WriteChecklistTemplates,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                FeatureFlagKeys: ["checklist_templates"],
                ChatToolNames: ["create_checklist_template", "update_checklist_template", "delete_checklist_template"],
                McpToolNames: ["create_checklist_template", "update_checklist_template", "delete_checklist_template"],
                ControllerActionKeys: ["ChecklistTemplatesController.CreateTemplate", "ChecklistTemplatesController.DeleteTemplate"])
        ];
    }

    private static AgentCapability[] ReferralCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.ReferralsRead,
                "Read Referrals",
                "Reads referral code, stats, and dashboard state.",
                "referrals",
                AgentScopes.ReadReferrals,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["get_referral_overview"],
                McpToolNames: ["get_referral_stats"],
                ControllerActionKeys:
                [
                    "ReferralController.GetStats",
                    "ReferralController.GetDashboard"
                ]),

            new AgentCapability(
                AgentCapabilityIds.ReferralsWrite,
                "Write Referrals",
                "Generates the user's referral code on demand.",
                "referrals",
                AgentScopes.WriteReferrals,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["get_referral_code"],
                McpToolNames: ["get_referral_code"],
                ControllerActionKeys: ["ReferralController.GetOrCreateCode"])
        ];
    }

    private static AgentCapability[] SubscriptionCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.SubscriptionsRead,
                "Read Subscription State",
                "Reads plans, billing status, and entitlement state.",
                "subscriptions",
                AgentScopes.ReadSubscriptions,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["get_subscription_overview"],
                McpToolNames: ["get_subscription_status"],
                ControllerActionKeys:
                [
                    "SubscriptionController.GetStatus",
                    "SubscriptionController.GetBillingDetails",
                    "SubscriptionController.GetPlans"
                ]),

            new AgentCapability(
                AgentCapabilityIds.SubscriptionsManage,
                "Manage Subscription",
                "Creates checkout and billing portal sessions.",
                "subscriptions",
                AgentScopes.ManageSubscriptions,
                AgentRiskClass.High,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.StepUp,
                ChatToolNames: ["manage_subscription"],
                McpToolNames: ["manage_subscription"],
                ControllerActionKeys:
                [
                    "SubscriptionController.CreateCheckout",
                    "SubscriptionController.CreatePortal",
                    "SubscriptionController.ClaimAdReward",
                    "SubscriptionController.HandleWebhook",
                    "SubscriptionController.VerifyPlayPurchase",
                    "SubscriptionController.HandlePlayNotification"
                ])
        ];
    }

    private static AgentCapability[] ApiKeyCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.ApiKeysRead,
                "Read API Keys",
                "Reads API-key metadata without exposing key material.",
                "api-keys",
                AgentScopes.ReadApiKeys,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                PlanRequirement: "Pro",
                FeatureFlagKeys: ["api_keys"],
                ChatToolNames: ["get_api_keys"],
                McpToolNames: ["get_api_keys", "get_api_key_page"],
                ControllerActionKeys: ["ApiKeysController.GetApiKeys", RecordListPageAction]),

            new AgentCapability(
                AgentCapabilityIds.ApiKeysManage,
                "Manage API Keys",
                "Creates and revokes scoped API keys.",
                "api-keys",
                AgentScopes.ManageApiKeys,
                AgentRiskClass.High,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.StepUp,
                PlanRequirement: "Pro",
                FeatureFlagKeys: ["api_keys"],
                ChatToolNames: ["manage_api_keys"],
                McpToolNames: ["manage_api_keys"],
                ControllerActionKeys:
                [
                    "ApiKeysController.CreateApiKey",
                    "ApiKeysController.RequestCreationChallenge",
                    "ApiKeysController.ConfirmCreationChallenge",
                    "ApiKeysController.RevokeApiKey"
                ])
        ];
    }

    private static AgentCapability[] SupportCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.SupportWrite,
                "Send Support Request",
                "Submits a support request on behalf of the user.",
                "support",
                AgentScopes.WriteSupport,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ChatToolNames: ["send_support_request"],
                McpToolNames: ["send_support_request"],
                ControllerActionKeys: ["SupportController.SendSupport"])
        ];
    }

    private static AgentCapability[] SyncCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.SyncRead,
                "Read Sync Data",
                "Reads versioned sync payloads for first-party clients.",
                "sync",
                AgentScopes.ReadSync,
                AgentRiskClass.Low,
                IsMutation: false,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ControllerActionKeys: ["SyncController.GetChangesV2", "EventsController.Stream", "EventsController.CreateTicket"]),

            new AgentCapability(
                AgentCapabilityIds.SyncWrite,
                "Write Sync Data",
                "Processes sync mutations from first-party clients.",
                "sync",
                AgentScopes.WriteSync,
                AgentRiskClass.Destructive,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.FreshConfirmation,
                ControllerActionKeys: ["SyncController.ProcessBatch"])
        ];
    }

    private static AgentCapability[] AccountAndAuthCapabilities()
    {
        return
        [
            new AgentCapability(
                AgentCapabilityIds.AccountManage,
                "Manage Account",
                "Resets account state or handles account deletion lifecycle.",
                "account",
                AgentScopes.ManageAccount,
                AgentRiskClass.High,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.StepUp,
                ChatToolNames: ["manage_account"],
                McpToolNames: ["manage_account"],
                ControllerActionKeys:
                [
                    "AuthController.RequestDeletion",
                    "AuthController.ConfirmDeletion",
                    "ProfileController.ResetAccount",
                    "ProfileController.ExportUserData"
                ]),

            new AgentCapability(
                AgentCapabilityIds.AuthManage,
                "Manage Authentication",
                "Sends codes, verifies sessions, refreshes, logs out, and runs OAuth exchange.",
                "auth",
                AgentScopes.ManageAuth,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ControllerActionKeys:
                [
                    "AuthController.SendCode",
                    "AuthController.SendCodeOperation",
                    "AuthController.VerifyCode",
                    "AuthController.VerifyCodeOperation",
                    "AuthController.GoogleCodeAuth",
                    "AuthController.GoogleCodeAuthOperation",
                    "AuthController.Refresh",
                    "AuthController.RefreshOperation",
                    "AuthController.Logout",
                    "AuthController.LogoutOperation",
                    "AuthController.LogoutAll",
                    "OAuthController.GetMetadata",
                    "OAuthController.Register",
                    "OAuthController.GetProtectedResourceMetadata",
                    "OAuthController.Authorize",
                    "OAuthController.Asset",
                    "OAuthController.SendCode",
                    "OAuthController.VerifyCode",
                    "OAuthController.GoogleStart",
                    "OAuthController.GoogleCallback",
                    "OAuthController.Token",
                    "OAuthController.Consent",
                    "OAuthController.GrantConsent",
                    "WaitlistController.Join",
                    "WaitlistController.Confirm"
                ]),

            new AgentCapability(
                AgentCapabilityIds.MediaUpload,
                "Upload Media",
                "Issues signed upload URLs and redirects public object reads to short-lived storage URLs.",
                "media",
                AgentScopes.UploadMedia,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ControllerActionKeys: ["UploadsController.SignUpload", "UploadsController.ReadObject"]),

            new AgentCapability(
                AgentCapabilityIds.MarketingManage,
                "Manage Marketing",
                "Sends admin marketing broadcasts and handles public one-click unsubscribe. Operator/anonymous endpoints, not agent-invocable.",
                "marketing",
                AgentScopes.ManageMarketing,
                AgentRiskClass.Low,
                IsMutation: true,
                IsPhaseOneReadOnly: false,
                AgentConfirmationRequirement.None,
                ControllerActionKeys:
                [
                    "AdminController.SendMarketingBroadcast",
                    "MarketingController.Unsubscribe",
                    "MarketingController.UnsubscribeOneClick",
                    "EmailController.SesEvents"
                ])
        ];
    }
}
