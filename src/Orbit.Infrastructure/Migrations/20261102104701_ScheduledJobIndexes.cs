using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbit.Infrastructure.Migrations;

public partial class ScheduledJobIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        CreateIndex(migrationBuilder, "IX_UserSessions_ExpiresAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_UserSessions_ExpiresAtUtc" ON "UserSessions" ("ExpiresAtUtc");""");
        CreateIndex(migrationBuilder, "IX_UserSessions_RevokedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_UserSessions_RevokedAtUtc" ON "UserSessions" ("RevokedAtUtc");""");
        CreateIndex(migrationBuilder, "IX_Users_AiMessagesLocalDate", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Users_AiMessagesLocalDate" ON "Users" ("AiMessagesLocalDate") WHERE "AiMessagesUsedToday" > 0;""");
        CreateIndex(migrationBuilder, "IX_Tags_DeletedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Tags_DeletedAtUtc" ON "Tags" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""");
        CreateIndex(migrationBuilder, "IX_SentSlipAlerts_WeekStart", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_SentSlipAlerts_WeekStart" ON "SentSlipAlerts" ("WeekStart");""");
        CreateIndex(migrationBuilder, "IX_SentReminders_Date", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_SentReminders_Date" ON "SentReminders" ("Date");""");
        CreateIndex(migrationBuilder, "IX_ProcessedStripeEvents_ProcessedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ProcessedStripeEvents_ProcessedAtUtc" ON "ProcessedStripeEvents" ("ProcessedAtUtc");""");
        CreateIndex(migrationBuilder, "IX_ProcessedPlayNotifications_ProcessedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ProcessedPlayNotifications_ProcessedAtUtc" ON "ProcessedPlayNotifications" ("ProcessedAtUtc");""");
        CreateIndex(migrationBuilder, "IX_ProactiveItems_LocalDate", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ProactiveItems_LocalDate" ON "ProactiveItems" ("LocalDate");""");
        CreateIndex(migrationBuilder, "IX_ProactiveCalendarDays_LocalDate", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ProactiveCalendarDays_LocalDate" ON "ProactiveCalendarDays" ("LocalDate");""");
        CreateIndex(migrationBuilder, "IX_PendingAgentOperations_ExpiresAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_PendingAgentOperations_ExpiresAtUtc" ON "PendingAgentOperations" ("ExpiresAtUtc");""");
        CreateIndex(migrationBuilder, "IX_OAuthStates_ExpiresAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_OAuthStates_ExpiresAtUtc" ON "OAuthStates" ("ExpiresAtUtc");""");
        CreateIndex(migrationBuilder, "IX_Notifications_CreatedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Notifications_CreatedAtUtc" ON "Notifications" ("CreatedAtUtc");""");
        CreateIndex(migrationBuilder, "IX_Notifications_DeletedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Notifications_DeletedAtUtc" ON "Notifications" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""");
        CreateIndex(migrationBuilder, "IX_Habits_DeletedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Habits_DeletedAtUtc" ON "Habits" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""");
        CreateIndex(migrationBuilder, "IX_Habits_NextDueDate", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Habits_NextDueDate" ON "Habits" ("NextDueDate") WHERE "IsCompleted" = FALSE AND "Schedule" = 1 AND "FrequencyQuantity" IS NOT NULL AND "IsBadHabit" = TRUE AND "IsDeleted" = FALSE;""");
        CreateIndex(migrationBuilder, "IX_HabitLogs_DeletedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_HabitLogs_DeletedAtUtc" ON "HabitLogs" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""");
        CreateIndex(migrationBuilder, "IX_GoogleCalendarSyncSuggestions_DiscoveredAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_GoogleCalendarSyncSuggestions_DiscoveredAtUtc" ON "GoogleCalendarSyncSuggestions" ("DiscoveredAtUtc") WHERE "ImportedAtUtc" IS NULL;""");
        CreateIndex(migrationBuilder, "IX_Goals_ActiveUser", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Goals_ActiveUser" ON "Goals" ("UserId") WHERE "Status" = 0 AND "IsDeleted" = FALSE;""");
        CreateIndex(migrationBuilder, "IX_Goals_DeletedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Goals_DeletedAtUtc" ON "Goals" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""");
        CreateIndex(migrationBuilder, "IX_Goals_Status_Deadline", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Goals_Status_Deadline" ON "Goals" ("Status", "Deadline") WHERE "Status" = 0 AND "IsDeleted" = FALSE;""");
        CreateIndex(migrationBuilder, "IX_GoalProgressLogs_DeletedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_GoalProgressLogs_DeletedAtUtc" ON "GoalProgressLogs" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""");
        CreateIndex(migrationBuilder, "IX_ChecklistTemplates_DeletedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ChecklistTemplates_DeletedAtUtc" ON "ChecklistTemplates" ("DeletedAtUtc") WHERE "IsDeleted" = TRUE;""");
        CreateIndex(migrationBuilder, "IX_ChatTurns_ExpiresAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ChatTurns_ExpiresAtUtc" ON "ChatTurns" ("ExpiresAtUtc");""");
        CreateIndex(migrationBuilder, "IX_AiBudgetCalls_LocalDate", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_AiBudgetCalls_LocalDate" ON "AiBudgetCalls" ("LocalDate");""");
        CreateIndex(migrationBuilder, "IX_AgentAuditLogs_CreatedAtUtc", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_AgentAuditLogs_CreatedAtUtc" ON "AgentAuditLogs" ("CreatedAtUtc");""");
        migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "IX_Habits_UserId";""", suppressTransaction: true);
    }

    private static void CreateIndex(MigrationBuilder migrationBuilder, string name, string sql)
    {
        migrationBuilder.Sql($"""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_catalog.pg_index
                    WHERE indexrelid = to_regclass('"{name}"') AND NOT indisvalid) THEN
                    EXECUTE 'DROP INDEX IF EXISTS "{name}"';
                END IF;
            END $$;
            """, suppressTransaction: true);
        migrationBuilder.Sql(sql, suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        CreateIndex(migrationBuilder, "IX_Habits_UserId", """CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_Habits_UserId" ON "Habits" ("UserId");""");
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
