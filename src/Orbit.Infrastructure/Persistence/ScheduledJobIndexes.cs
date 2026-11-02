using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

internal static class ScheduledJobIndexes
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Goal>().HasIndex(row => new { row.Status, row.Deadline })
            .HasFilter("\"Status\" = 0 AND \"IsDeleted\" = FALSE");
        modelBuilder.Entity<Goal>().HasIndex(row => row.UserId, "IX_Goals_ActiveUser")
            .HasFilter("\"Status\" = 0 AND \"IsDeleted\" = FALSE");
        modelBuilder.Entity<Habit>().HasIndex(row => row.NextDueDate)
            .HasFilter("\"IsCompleted\" = FALSE AND \"Schedule\" = 1 AND \"FrequencyQuantity\" IS NOT NULL AND \"IsBadHabit\" = TRUE AND \"IsDeleted\" = FALSE");
        modelBuilder.Entity<User>().HasIndex(row => row.AiMessagesLocalDate)
            .HasFilter("\"AiMessagesUsedToday\" > 0");
        modelBuilder.Entity<Habit>().HasIndex(row => row.DeletedAtUtc).HasFilter("\"IsDeleted\" = TRUE");
        modelBuilder.Entity<Goal>().HasIndex(row => row.DeletedAtUtc).HasFilter("\"IsDeleted\" = TRUE");
        modelBuilder.Entity<Tag>().HasIndex(row => row.DeletedAtUtc).HasFilter("\"IsDeleted\" = TRUE");
        modelBuilder.Entity<HabitLog>().HasIndex(row => row.DeletedAtUtc).HasFilter("\"IsDeleted\" = TRUE");
        modelBuilder.Entity<GoalProgressLog>().HasIndex(row => row.DeletedAtUtc).HasFilter("\"IsDeleted\" = TRUE");
        modelBuilder.Entity<Notification>().HasIndex(row => row.DeletedAtUtc).HasFilter("\"IsDeleted\" = TRUE");
        modelBuilder.Entity<ChecklistTemplate>().HasIndex(row => row.DeletedAtUtc).HasFilter("\"IsDeleted\" = TRUE");
        modelBuilder.Entity<GoogleCalendarSyncSuggestion>().HasIndex(row => row.DiscoveredAtUtc)
            .HasFilter("\"ImportedAtUtc\" IS NULL");
        modelBuilder.Entity<ChatTurn>().HasIndex(row => row.ExpiresAtUtc);
        modelBuilder.Entity<SentReminder>().HasIndex(row => row.Date);
        modelBuilder.Entity<SentSlipAlert>().HasIndex(row => row.WeekStart);
        modelBuilder.Entity<ProcessedPlayNotification>().HasIndex(row => row.ProcessedAtUtc);
        modelBuilder.Entity<ProcessedStripeEvent>().HasIndex(row => row.ProcessedAtUtc);
        modelBuilder.Entity<OAuthState>().HasIndex(row => row.ExpiresAtUtc);
        modelBuilder.Entity<Notification>().HasIndex(row => row.CreatedAtUtc);
        modelBuilder.Entity<AiBudgetCall>().HasIndex(row => row.LocalDate);
        modelBuilder.Entity<PendingAgentOperationState>().HasIndex(row => row.ExpiresAtUtc);
        modelBuilder.Entity<UserSession>().HasIndex(row => row.ExpiresAtUtc);
        modelBuilder.Entity<UserSession>().HasIndex(row => row.RevokedAtUtc);
        modelBuilder.Entity<IssuedAstraBlock>().HasIndex(row => row.ExpiresAtUtc);
        modelBuilder.Entity<PendingClarification>().HasIndex(row => row.ExpiresAtUtc);
        modelBuilder.Entity<AgentAuditLog>().HasIndex(row => row.CreatedAtUtc);
        modelBuilder.Entity<DesktopHandoff>().HasIndex(row => row.ExpiresAtUtc);
        modelBuilder.Entity<ProactiveItem>().HasIndex(row => row.LocalDate);
        modelBuilder.Entity<ProactiveCalendarDay>().HasIndex(row => row.LocalDate);
    }
}
