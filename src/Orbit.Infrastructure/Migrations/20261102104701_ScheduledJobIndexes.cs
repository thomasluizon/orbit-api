using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations;

public partial class ScheduledJobIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_UserSessions_ExpiresAtUtc" ON "UserSessions" ("ExpiresAtUtc");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_UserSessions_RevokedAtUtc" ON "UserSessions" ("RevokedAtUtc");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Users_AiMessagesLocalDate" ON "Users" ("AiMessagesLocalDate") WHERE "AiMessagesUsedToday" > 0;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Tags_DeletedAtUtc" ON "Tags" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_SentSlipAlerts_WeekStart" ON "SentSlipAlerts" ("WeekStart");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_SentReminders_Date" ON "SentReminders" ("Date");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ProcessedStripeEvents_ProcessedAtUtc" ON "ProcessedStripeEvents" ("ProcessedAtUtc");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ProcessedPlayNotifications_ProcessedAtUtc" ON "ProcessedPlayNotifications" ("ProcessedAtUtc");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ProactiveItems_LocalDate" ON "ProactiveItems" ("LocalDate");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ProactiveCalendarDays_LocalDate" ON "ProactiveCalendarDays" ("LocalDate");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_PendingAgentOperations_ExpiresAtUtc" ON "PendingAgentOperations" ("ExpiresAtUtc");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_OAuthStates_ExpiresAtUtc" ON "OAuthStates" ("ExpiresAtUtc");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Notifications_CreatedAtUtc" ON "Notifications" ("CreatedAtUtc");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Notifications_DeletedAtUtc" ON "Notifications" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Habits_DeletedAtUtc" ON "Habits" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Habits_NextDueDate" ON "Habits" ("NextDueDate") WHERE "IsCompleted" = FALSE AND "Schedule" = 1 AND "FrequencyQuantity" IS NOT NULL AND "IsBadHabit" = TRUE AND "IsDeleted" = FALSE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_HabitLogs_DeletedAtUtc" ON "HabitLogs" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_GoogleCalendarSyncSuggestions_DiscoveredAtUtc" ON "GoogleCalendarSyncSuggestions" ("DiscoveredAtUtc") WHERE "ImportedAtUtc" IS NULL;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Goals_ActiveUser" ON "Goals" ("UserId") WHERE "Status" = 0 AND "IsDeleted" = FALSE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Goals_DeletedAtUtc" ON "Goals" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Goals_Status_Deadline" ON "Goals" ("Status", "Deadline") WHERE "Status" = 0 AND "IsDeleted" = FALSE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_GoalProgressLogs_DeletedAtUtc" ON "GoalProgressLogs" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ChecklistTemplates_DeletedAtUtc" ON "ChecklistTemplates" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ChatTurns_ExpiresAtUtc" ON "ChatTurns" ("ExpiresAtUtc");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_AiBudgetCalls_LocalDate" ON "AiBudgetCalls" ("LocalDate");""", suppressTransaction: true);
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_AgentAuditLogs_CreatedAtUtc" ON "AgentAuditLogs" ("CreatedAtUtc");""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Habits_UserId";""", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Habits_UserId" ON "Habits" ("UserId");""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_AgentAuditLogs_CreatedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_AiBudgetCalls_LocalDate";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_ChatTurns_ExpiresAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_ChecklistTemplates_DeletedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_GoalProgressLogs_DeletedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Goals_Status_Deadline";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Goals_DeletedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Goals_ActiveUser";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_GoogleCalendarSyncSuggestions_DiscoveredAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_HabitLogs_DeletedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Habits_NextDueDate";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Habits_DeletedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Notifications_DeletedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Notifications_CreatedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_OAuthStates_ExpiresAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_PendingAgentOperations_ExpiresAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_ProactiveCalendarDays_LocalDate";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_ProactiveItems_LocalDate";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_ProcessedPlayNotifications_ProcessedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_ProcessedStripeEvents_ProcessedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_SentReminders_Date";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_SentSlipAlerts_WeekStart";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Tags_DeletedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Users_AiMessagesLocalDate";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_UserSessions_RevokedAtUtc";""", suppressTransaction: true);
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_UserSessions_ExpiresAtUtc";""", suppressTransaction: true);
    }
}
