const chat = "src/Orbit.Application/Chat/Tools/Implementations/"

// Semantic bindings connect parameters and output projections; verdicts and member evidence are generated.
export const entities = {
  Habit: { model: "Habit", editableRecord: "HabitUpdateParams", tools: {
    create: ["create_habit"], read: ["query_habits", "get_habit", "list_habits"],
    update: ["update_habit", "update_checklist", "assign_tags", "move_habit", "move_habit_parent", "link_goals_to_habit", "reorder_habits"],
    delete: ["delete_habit"], bulk: ["bulk_create_habits", "bulk_delete_habits", "bulk_update_habits", "bulk_reschedule_habits", "bulk_update_habit_emojis", "reorder_habits"],
    filterKeyedBulk: ["bulk_update_habits", "bulk_reschedule_habits", "bulk_delete_habits", "bulk_log_habits", "bulk_skip_habits", "bulk_update_habit_emojis"] } },
  SubHabit: { model: "Habit", editableRecord: "HabitUpdateParams", tools: {
    create: ["create_sub_habit"], read: ["query_habits", "get_habit", "list_habits"],
    update: ["update_habit", "update_checklist", "assign_tags", "move_habit_parent", "link_goals_to_habit", "reorder_habits"],
    delete: ["delete_habit"], bulk: ["bulk_create_habits", "bulk_delete_habits", "bulk_update_habits", "bulk_reschedule_habits", "reorder_habits"],
    filterKeyedBulk: ["bulk_update_habits", "bulk_reschedule_habits", "bulk_delete_habits", "bulk_log_habits", "bulk_skip_habits"] } },
  HabitLog: { model: "HabitLog", tools: { create: ["log_habit"], read: ["get_habit_logs", "get_all_habit_logs"], delete: ["unlog_habit"], bulk: ["bulk_log_habits"], filterKeyedBulk: ["bulk_log_habits"] },
    aliases: { HabitId: ["habit_id", "habitId"], Date: ["date"] } },
  Goal: { model: "Goal", tools: { create: ["create_goal"], read: ["query_goals", "get_goal", "list_goals"], update: ["update_goal", "update_goal_progress", "update_goal_status", "link_habits_to_goal", "reorder_goals"], delete: ["delete_goal"], bulk: ["reorder_goals"] }, aliases: { Type: ["goal_type", "type"], Habits: ["habit_ids", "habitIds"] } },
  Tag: { model: "Tag", tools: { create: ["create_tag"], read: ["list_tags", "get_tag_page"], update: ["update_tag"], delete: ["delete_tag"] } },
  ChecklistTemplate: { model: "ChecklistTemplate", tools: { create: ["create_checklist_template"], read: ["get_checklist_templates", "get_template_page"], update: ["update_checklist_template"], delete: ["delete_checklist_template"] } },
  Notification: { model: "Notification", tools: { read: ["get_notifications", "get_notification_page"], update: ["update_notifications", "mark_notification_read", "mark_all_notifications_read"], delete: ["delete_notifications", "delete_notification"], bulk: ["update_notifications", "delete_notifications", "mark_all_notifications_read"] }, aliases: { IsRead: ["action"] } },
  PushSubscription: { model: "PushSubscription", tools: { create: ["update_notifications", "subscribe_push"], delete: ["update_notifications", "unsubscribe_push"] } },
  Profile: { model: "User", editableMethods: { Name: "SetName", MarketingEmailConsent: "SetMarketingConsent" }, tools: { read: ["get_profile"], update: ["update_profile_preferences", "set_name", "set_timezone", "set_language", "set_week_start_day", "set_clock_format", "set_ai_summary", "set_proactive_astra"], delete: ["manage_account"] }, aliases: { TimeZone: ["timezone"], Uses24HourClockPreference: ["uses_24_hour_clock", "uses24HourClock"], AiSummaryEnabled: ["enabled"], ProactiveAstraEnabled: ["enabled"], HasCompletedOnboarding: ["action"], HasCompletedTour: ["action"] } },
  Subscription: { model: "User", select: /^(Plan|Trial|BonusPro|IsLifetimePro|Subscription|Stripe|Play|AiMessages)/, tools: { read: ["get_subscription_overview", "get_subscription_status"], create: ["manage_subscription"], update: ["manage_subscription"] }, aliases: { SubscriptionInterval: ["interval"] } },
  Referral: { model: "Referral", tools: { read: ["get_referral_overview", "get_referral_stats", "get_referral_code"], create: ["get_referral_code"] } },
  ApiKey: { model: "ApiKey", tools: { create: ["manage_api_keys"], read: ["get_api_keys", "get_api_key_page"], delete: ["manage_api_keys"] } },
  CalendarSync: { model: "User", editableMethods: { GoogleCalendarSelectedIds: "SetSelectedCalendars" }, select: /^(Google|HasImportedCalendar|HasSeenImportPrompt)/, tools: { read: ["get_calendar_overview", "get_profile"], update: ["manage_calendar_sync", "set_selected_calendars"] }, aliases: { GoogleCalendarAutoSyncEnabled: ["enabled"], GoogleCalendarSelectedIds: ["calendar_ids", "calendarIds"], HasSeenImportPrompt: ["action"] } },
  CalendarSyncSuggestion: { model: "GoogleCalendarSyncSuggestion", tools: { read: ["get_calendar_overview"], update: ["manage_calendar_sync"] } },
  Gamification: { model: "User", select: /^(TotalXp|Level|CurrentStreak|LongestStreak|LastActiveDate|LastPurgedCompletionDate|StreakFreezes|LastFreeze|PreGap)/, deliberate: "Rewards, streaks and XP are earned through domain operations, not arbitrary AI writes.", tools: { read: ["get_gamification_overview", "get_gamification_profile", "get_streak_info", "get_profile"] } },
  UserAchievement: { model: "UserAchievement", deliberate: "Achievements are awarded by the gamification service, not arbitrary AI writes.", tools: { read: ["get_gamification_overview", "get_achievements"] } },
  StreakFreeze: { model: "StreakFreeze", deliberate: "Freezes are earned and consumed by the streak service, not arbitrary AI writes.", tools: { read: ["get_gamification_overview", "get_streak_info"] } },
  XpAwardLog: { model: "XpAwardLog", deliberate: "XP award records are service-owned audit state, not arbitrary AI writes.", tools: {} },
}

export const aliases = {
  NextDueDate: ["due_date", "dueDate"],
  Schedule: ["is_general", "isGeneral", "is_flexible", "isFlexible", "frequency_unit", "frequencyUnit"],
  Tags: ["tags", "tag_names", "tag_ids", "tagIds"], Goals: ["goal_ids", "goalIds"],
  ParentHabitId: ["parent_habit_id", "parent_id", "parentHabitId", "parentId"],
  ChecklistItems: ["checklist_items", "checklistItemsJson"],
  Position: ["positions", "positionsJson"],
}

export const parameterRestrictions = {
  AiSummaryEnabled: ["set_ai_summary"], ProactiveAstraEnabled: ["set_proactive_astra"],
  HasCompletedOnboarding: ["update_profile_preferences"], HasCompletedTour: ["update_profile_preferences"],
  IsRead: ["update_notifications", "mark_notification_read", "mark_all_notifications_read"],
}

export const actions = {
  "PushSubscription:create": "subscribe_push", "PushSubscription:delete": "unsubscribe_push",
  "Notification:update": "mark_read|mark_all_read", "Notification:delete": "delete_one|delete_all",
  "Notification:bulk": "mark_all_read|delete_all", "ApiKey:create": "create", "ApiKey:delete": "revoke",
  "Profile:delete": "request_deletion|confirm_deletion", "Subscription:create": "create_checkout", "Subscription:update": "create_portal",
  "CalendarSync:update": "set_selected_calendars|set_auto_sync|dismiss_import|run_sync", "CalendarSyncSuggestion:update": "dismiss_suggestion",
}

// DTO bindings are only for payloads actually returned by the named tool, not intermediate queries.
export const payloads = {
  "chat:get_profile": ["ProfileResponse"],
  "chat:list_tags": ["TagResponse"],
  "chat:get_checklist_templates": ["ChecklistTemplateResponse"],
  "chat:get_notifications": ["NotificationItemDto"],
  "chat:get_api_keys": ["ApiKeyResponse"],
  "chat:get_subscription_overview": ["SubscriptionStatusResponse", "BillingDetailsResponse"],
  "chat:get_referral_overview": ["ReferralDashboardResponse", "ReferralStatsResponse"],
  "chat:get_calendar_overview": ["CalendarAutoSyncStateResponse", "CalendarSyncSuggestionItem", "CalendarEventItem"],
  "mcp:get_calendar_overview": ["CalendarAutoSyncStateResponse", "CalendarSyncSuggestionItem", "CalendarEventItem"],
  "chat:get_gamification_overview": ["GamificationProfileResponse", "AchievementDto", "StreakInfoResponse"],
}

export const outputAliases = {
  Id: ["UserId"],
  SubscriptionSource: ["Source"], SubscriptionLapseReason: ["LapseReason"],
  Uses24HourClockPreference: ["Uses24HourClock"], AiMessagesUsedToday: ["AiMessagesUsed"],
  GoogleCalendarAutoSyncEnabled: ["Enabled"], GoogleCalendarAutoSyncStatus: ["Status"], GoogleCalendarLastSyncedAt: ["LastSyncedAt"],
  AchievementId: ["Id"], EarnedAtUtc: ["EarnedAt"], UsedOnDate: ["RecentFreezeDates"],
}

// Select the rendered expressions, never a predicate or an intermediate projection.
export const rendered = {
  "chat:query_habits": { file: chat + "QueryHabitsTool.cs", start: "private static string BuildHabitLine", receiver: "habit", fields: "Id Title Emoji NextDueDate FrequencyUnit FrequencyQuantity IsBadHabit Schedule IsCompleted", partial: "ChecklistItems Tags" },
  "chat:query_goals": { file: chat + "QueryGoalsTool.cs", start: "private static string BuildOutput", receiver: "goal", fields: "Id Title Description Type Status TargetValue CurrentValue Unit Deadline", partial: "Habits" },
  "mcp:get_habit": { receiver: "h", names: { NextDueDate: "DueDate" }, fields: "Id Title Emoji Description FrequencyUnit FrequencyQuantity NextDueDate DueTime Days IsBadHabit Schedule IsCompleted CreatedAtUtc", partial: "ChecklistItems Children" },
  "mcp:list_habits": { receiver: "h", fields: "Id Title Emoji FrequencyUnit FrequencyQuantity DueTime IsCompleted IsBadHabit Schedule", partial: "ChecklistItems Children Tags" },
  "mcp:get_goal": { receiver: "g", fields: "Id Title Description Status CurrentValue TargetValue Unit Deadline CreatedAtUtc CompletedAtUtc", partial: "Habits" },
  "mcp:list_goals": { receiver: "g", fields: "Id Title Status CurrentValue TargetValue Unit Deadline" },
  "mcp:list_tags": { receiver: "t", fields: "Id Name Color" },
  "mcp:get_checklist_templates": { receiver: "t", fields: "Id Name Items" },
  "mcp:get_notifications": { receiver: "n", fields: "Id Title IsRead CreatedAtUtc" },
  "mcp:get_api_keys": { receiver: "k", fields: "Id Name KeyPrefix Scopes IsReadOnly ExpiresAtUtc IsRevoked" },
  "mcp:get_profile": { receiver: "p", fields: "Name Email Plan TrialEndsAt TimeZone Language Level TotalXp WeekStartDay Uses24HourClockPreference AiMessagesUsedToday", names: { Uses24HourClockPreference: "Uses24HourClock", AiMessagesUsedToday: "AiMessagesUsed" } },
  "mcp:get_subscription_status": { receiver: "u", fields: "Plan TrialEndsAt PlanExpiresAt SubscriptionInterval IsLifetimePro AiMessagesUsedToday", names: { Plan: "HasProAccess", AiMessagesUsedToday: "GetAiMessagesUsedToday" } },
  "mcp:get_habit_logs": { receiver: "l", fields: "Id Date" },
  "mcp:get_all_habit_logs": { receiver: "log", fields: "Date" },
  "mcp:get_gamification_profile": { receiver: "p", fields: "TotalXp Level CurrentStreak LongestStreak LastActiveDate" },
  "mcp:get_streak_info": { receiver: "s", fields: "CurrentStreak LongestStreak LastActiveDate", partial: "UsedOnDate" },
}

const summary = "src/Orbit.Application/Habits/Queries/HabitFieldSummary.cs"
const habits = "src/Orbit.Api/Mcp/Tools/HabitTools.cs"
const helpers = "src/Orbit.Api/Mcp/Tools/McpToolHelpers.cs"
const step = (file, member, expression) => ({ file, member, expression })
const format = expression => step(summary, "HabitFieldSummary.Format", expression)
const clock = [step(summary, "HabitFieldSummary.FormatClock", "when == ScheduledReminderWhen.DayBefore"), step(summary, "HabitFieldSummary.FormatClock", "time?.ToString")]
const checklist = [format("ChecklistItems.Select(FormatChecklistItem)"), step(summary, "HabitFieldSummary.FormatChecklistItem", "item.IsChecked"), step(summary, "HabitFieldSummary.FormatChecklistItem", "item.Text")]
const relative = [format("RelativeReminders.Select(FormatRelative)"), step(summary, "HabitFieldSummary.FormatRelative", "reminder.MinutesBefore"), step(summary, "HabitFieldSummary.FormatRelative", "FormatClock(reminder.When, reminder.Time)"), ...clock]
const scheduled = [format("ScheduledReminders.Select(r => FormatClock(r.When, r.Time))"), ...clock]
const formats = {
  StartDate: [format("StartDate is { } startDate"), format('fields.Add($"Start date:')],
  ParentHabitId: [format("ParentHabitId is { } parentId"), format('{parentId}')],
  Tags: [format('Tags.Select(t => $"{t.Name} ({t.Id})")')],
  Description: [format('fields.Add($"Description: {Description}")')],
  DueTime: [format("DueTime is { } time"), format('fields.Add($"Due time:')],
  DueEndTime: [format("DueEndTime is { } endTime"), format('fields.Add($"Due end time:')],
  IntervalWeeks: [format("IntervalWeeks is { } weeks"), format('fields.Add($"Interval weeks:')],
  Days: [format('string.Join(", ", Days)')],
  EndDate: [format("EndDate is { } endDate"), format('fields.Add($"End date:')],
  ReminderEnabled: [format('if (ReminderEnabled) fields.Add("Reminders: enabled")')],
  ReminderTimes: [format('string.Join(", ", ReminderTimes)')],
  ScheduledReminders: scheduled, "ScheduledReminders.When": scheduled, "ScheduledReminders.Time": scheduled,
  RelativeReminders: relative, "RelativeReminders.MinutesBefore": relative, "RelativeReminders.When": relative, "RelativeReminders.Time": relative,
  SlipAlertEnabled: [format('if (SlipAlertEnabled) fields.Add("Slip alerts: enabled")')],
  ChecklistItems: checklist, "ChecklistItems.Text": checklist, "ChecklistItems.IsChecked": checklist,
}
const summaryChains = (source, member, receiver, common, fields = Object.keys(formats)) => Object.fromEntries(fields.map(field => [field, { steps: [
  ...common,
  step(source, member, `${field.split(".")[0]} = ${receiver}.${field.split(".")[0]}`),
  ...(field === "Tags" && source === summary ? [step(summary, "HabitFieldSummary.From", "habit.Tags.Select(t => new HabitTagItem(t.Id, t.Name, t.Color))")] : []),
  ...formats[field],
  format('return string.Join(" | ", fields)'),
] }]))
const querySummary = summaryChains(summary, "HabitFieldSummary.From", "habit", [
  step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildHabitLine", "HabitFieldSummary.From(habit, habit.ParentHabitId is { } parentId ? parentTitles.GetValueOrDefault(parentId) : null).Format()"),
  step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildHabitLine", "{detailSuffix}"),
])
const detailSummary = summaryChains(habits, "HabitTools.GetHabit", "h", [
  step(habits, "HabitTools.GetHabit", "}.Format()"), step(habits, "HabitTools.GetHabit", "habitSummary += details"),
], Object.keys(formats).filter(field => !["Description", "DueTime", "Days"].includes(field)))
const listSummary = summaryChains(habits, "HabitTools.ListHabits", "h", [
  step(habits, "HabitTools.ListHabits", "lines.Add(McpToolHelpers.FormatHabitLine"),
  step(habits, "HabitTools.ListHabits", "}.Format(h.DueDate)"),
  step(helpers, "McpToolHelpers.FormatHabitLine", 'line += $" | {data.Details}"'),
])
const calendarSelection = { steps: [
  step("src/Orbit.Application/Calendar/Queries/GetCalendarAutoSyncStateQuery.cs", "GetCalendarAutoSyncStateQueryHandler.Handle", "SelectedCalendarIds: user.GetSelectedCalendarIds()"),
  step("src/Orbit.Application/Calendar/Queries/GetCalendarAutoSyncStateQuery.cs", "GetCalendarAutoSyncStateQueryHandler.Handle", 'SelectionMode: user.GetSelectedCalendarIds() is null ? "default" : "explicit"'),
  step(chat + "CalendarTools.cs", "GetCalendarOverviewTool.ExecuteAsync", "mediator.Send(new GetCalendarAutoSyncStateQuery(userId), ct)"),
  step(chat + "CalendarTools.cs", "GetCalendarOverviewTool.ExecuteAsync", "Payload: AddSelection(new CalendarOverviewPayload("),
  step(chat + "CalendarTools.cs", "GetCalendarOverviewTool.AddSelection", "SelectedCalendarIds = payload.AutoSyncState?.SelectedCalendarIds"),
  step(chat + "CalendarTools.cs", "GetCalendarOverviewTool.AddSelection", "SelectionMode = payload.AutoSyncState?.SelectionMode"),
] }

export const readChains = {
  "chat:query_goals": { Habits: { steps: [
    step(chat + "QueryGoalsTool.cs", "QueryGoalsTool.BuildOutput", 'goal.Habits.Select(h => $"{h.Title} ({h.Id})")'),
    step(chat + "QueryGoalsTool.cs", "QueryGoalsTool.BuildOutput", 'Linked habits: {linkedHabits}'),
  ] } },
  "mcp:get_goal": { Habits: { steps: [
    step("src/Orbit.Api/Mcp/Tools/GoalTools.cs", "GoalTools.GetGoal", 'g.LinkedHabits.Select(h => $"{h.Title} ({h.Id})")'),
    step("src/Orbit.Application/Goals/Queries/GoalDetailLoader.cs", "GoalDetailLoader.BuildGoalDetailAsync", "new LinkedHabitDto(h.Id, h.Title)"),
  ] } },
  "mcp:list_goals": { Habits: { steps: [
    step("src/Orbit.Api/Mcp/Tools/GoalTools.cs", "GoalTools.ListGoals", 'g.LinkedHabits.Select(h => $"{h.Title} ({h.Id})")'),
    step("src/Orbit.Application/Goals/Queries/GetGoalsQuery.cs", "GetGoalsQueryHandler.MapToDto", "new LinkedHabitDto(h.Id, h.Title)"),
  ] } },
  "chat:query_habits": { ...querySummary, Schedule: { steps: [
    step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildHabitLine", "{freqLabel}"),
    step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.FormatFrequencyLabel", "habit.Schedule is HabitSchedule.OneTime or HabitSchedule.General"),
    step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildHabitLine", "{detailSuffix}"),
    step(summary, "HabitFieldSummary.From", "Schedule = habit.Schedule"),
    format('if (Schedule == HabitSchedule.Flexible) fields.Add("Flexible: true")'),
    step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildHabitLine", "BuildLabels(habit,"),
    step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildHabitLine", "{labelStr}"),
    step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildLabels", 'if (habit.Schedule == HabitSchedule.General) labels.Add("GENERAL")'),
  ] }, Goals: { steps: [
    step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildHabitLine", "BuildLabels(habit,"),
    step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildHabitLine", "{labelStr}"),
    step(chat + "QueryHabitsTool.cs", "QueryHabitsTool.BuildLabels", 'habit.Goals.Select(g => $"{g.Title} ({g.Id})")'),
  ] } },
  "mcp:get_habit": { ...detailSummary, Schedule: { steps: [
    step(habits, "HabitTools.GetHabit", 'h.Schedule == HabitSchedule.General ? "General: Yes'),
    step(habits, "HabitTools.GetHabit", 'h.Schedule == HabitSchedule.Flexible ? "Flexible: Yes'),
    step(habits, "HabitTools.GetHabit", 'h.FrequencyUnit is not null ? string.Create'),
  ] }, Goals: { steps: [
    step(habits, "HabitTools.GetHabit", 'habitSummary += $"Goals:'),
    step(habits, "HabitTools.GetHabit", 'h.LinkedGoals.Select(g => $"{g.Title} ({g.Id})")'),
    step("src/Orbit.Application/Habits/Queries/GetHabitByIdQuery.cs", "GetHabitByIdQueryHandler.Handle", "LinkedGoals: habit.Goals.Select(g => new LinkedGoalDto(g.Id, g.Title)).ToList()"),
  ] } },
  "mcp:list_habits": { ...listSummary, Schedule: { steps: [
    step(habits, "HabitTools.ListHabits", "h.Schedule ?? HabitSchedule.OneTime"),
    step(habits, "HabitTools.ListHabits", "McpToolHelpers.FormatHabitLine"),
    step(helpers, "McpToolHelpers.FormatHabitLine", 'data.Schedule != HabitSchedule.General) line += " | one-time"'),
    step(helpers, "McpToolHelpers.FormatHabitLine", 'data.Schedule == HabitSchedule.General) line += " | general"'),
    step(helpers, "McpToolHelpers.FormatHabitLine", 'data.Schedule == HabitSchedule.Flexible) line += " | flexible"'),
    step(helpers, "McpToolHelpers.FormatHabitLine", 'data.FreqUnit is not null) line +='),
  ] } },
  "chat:get_calendar_overview": { GoogleCalendarSelectedIds: calendarSelection },
  "mcp:get_calendar_overview": { GoogleCalendarSelectedIds: { steps: [
    ...calendarSelection.steps,
    step("src/Orbit.Api/Mcp/Tools/CalendarTools.cs", "CalendarTools.GetCalendarOverview", "JsonSerializer.Serialize(result.Payload)"),
  ] } },
}

export const deliberateFields = {
  "Habit.Position": "Display order is managed through reorder operations; scalar position readback is not an AI field-editing capability.",
  "SubHabit.Position": "Display order is managed through reorder operations; scalar position readback is not an AI field-editing capability.",
  "Goal.Position": "Display order is managed through reorder operations; scalar position readback is not an AI field-editing capability.",
  "HabitLog.HabitId": "The habit and date define a completion's identity. Undo and log again to change that identity rather than editing it in place.",
  "HabitLog.Date": "The habit and date define a completion's identity. Undo and log again to change that identity rather than editing it in place.",
  "Goal.Type": "Goal type is fixed at creation because it defines how progress is derived; create a replacement to change it.",
  "Profile.Email": "An email change requires the account email verification flow; AI tools do not bypass that verification.",
  "Profile.MarketingEmailConsent": "Marketing consent requires an explicit user choice in the account consent flow; an AI tool does not provide that consent.",
  "PushSubscription.Endpoint": "Push endpoints are device credentials. Only hashed endpoint metadata is exposed by the account API; AI tools do not read credentials back.",
  "PushSubscription.P256dh": "Device public-key material is managed through subscribe/unsubscribe, not AI readback.",
  "PushSubscription.Auth": "Device authentication material must not be read back to an AI client.",
  "ApiKey.KeyHash": "Only the one-time raw key can be delivered at creation. Persisted hashes are never returned.",
  "ApiKey.Name": "API key metadata is immutable after creation; revoke and create a replacement.",
  "ApiKey.Scopes": "API key scope grants are immutable; revoke and create a replacement.",
  "ApiKey.IsReadOnly": "The access mode is immutable; revoke and create a replacement.",
  "ApiKey.ExpiresAtUtc": "Expiry is immutable; revoke and create a replacement.",
}

export const forms = {
  CreateHabitRequest: { tools: ["create_habit"], aliases: { TagIds: ["tag_ids"], SubHabits: ["sub_habits"] } },
  UpdateHabitRequest: { tools: ["update_habit"], aliases: { ClearEndDate: ["clear_fields"], ParentId: ["parent_habit_id"], ClearParent: ["clear_parent"] } },
  CreateSubHabitRequest: { tools: ["create_sub_habit"] },
  UpdateChecklistRequest: { tools: ["update_checklist"] },
  CreateGoalRequest: { tools: ["create_goal"], aliases: { Type: ["goal_type"] } },
  UpdateGoalRequest: { tools: ["update_goal"] },
  UpdateProgressRequest: { tools: ["update_goal_progress"] },
  UpdateStatusRequest: { tools: ["update_goal_status"] },
  CreateTagRequest: { tools: ["create_tag"] },
  UpdateTagRequest: { tools: ["update_tag"] },
}
