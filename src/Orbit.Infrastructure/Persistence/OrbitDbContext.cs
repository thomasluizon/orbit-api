using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Persistence;

public class OrbitDbContext : DbContext
{
    private const string JsonbColumnType = "jsonb";
    private const string EmptyJsonArraySql = "'[]'::jsonb";
    private readonly IEncryptionService? _encryptionService;

    public OrbitDbContext(DbContextOptions<OrbitDbContext> options, IEncryptionService? encryptionService = null)
        : base(options)
    {
        _encryptionService = encryptionService;
    }

    /// <summary>
    /// Indicates whether this context was constructed with an encryption service.
    /// Used by tests to distinguish cached model variants.
    /// </summary>
    public bool HasEncryptionService => _encryptionService is not null;

    public DbSet<User> Users => Set<User>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<HabitLog> HabitLogs => Set<HabitLog>();
    public DbSet<UserFact> UserFacts => Set<UserFact>();
    public DbSet<AppConfig> AppConfigs => Set<AppConfig>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<SentReminder> SentReminders => Set<SentReminder>();
    public DbSet<SentSlipAlert> SentSlipAlerts => Set<SentSlipAlert>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<Goal> Goals => Set<Goal>();
    public DbSet<GoalProgressLog> GoalProgressLogs => Set<GoalProgressLog>();
    public DbSet<Referral> Referrals => Set<Referral>();
    public DbSet<UserAchievement> UserAchievements => Set<UserAchievement>();
    public DbSet<StreakFreeze> StreakFreezes => Set<StreakFreeze>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<PendingAgentOperationState> PendingAgentOperations => Set<PendingAgentOperationState>();
    public DbSet<AgentStepUpChallengeState> AgentStepUpChallenges => Set<AgentStepUpChallengeState>();
    public DbSet<AgentAuditLog> AgentAuditLogs => Set<AgentAuditLog>();
    public DbSet<DistributedRateLimitBucket> DistributedRateLimitBuckets => Set<DistributedRateLimitBucket>();
    public DbSet<ChecklistTemplate> ChecklistTemplates => Set<ChecklistTemplate>();
    public DbSet<AppFeatureFlag> AppFeatureFlags => Set<AppFeatureFlag>();
    public DbSet<ContentBlock> ContentBlocks => Set<ContentBlock>();
    public DbSet<GoogleCalendarSyncSuggestion> GoogleCalendarSyncSuggestions => Set<GoogleCalendarSyncSuggestion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var usePostgresArrayColumns = Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        // --- Encryption Value Converters ---
        EncryptionValueConverter? encConverter = null;
        NullableEncryptionValueConverter? nullableEncConverter = null;

        if (_encryptionService is not null)
        {
            encConverter = new EncryptionValueConverter(_encryptionService);
            nullableEncConverter = new NullableEncryptionValueConverter(_encryptionService);
        }
        ConfigureUserEntity(modelBuilder, nullableEncConverter);
        ConfigureHabitEntity(modelBuilder, usePostgresArrayColumns, encConverter, nullableEncConverter);
        ConfigureHabitLogEntity(modelBuilder, nullableEncConverter);
        ConfigureUserFactEntity(modelBuilder, encConverter);
        ConfigureGoogleCalendarSyncSuggestionEntity(modelBuilder, encConverter, nullableEncConverter);

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.Name }).IsUnique();
            entity.HasIndex(t => new { t.UserId, t.IsDeleted });
            entity.HasQueryFilter(t => !t.IsDeleted);

            entity.HasMany(t => t.Habits)
                .WithMany(h => h.Tags)
                .UsingEntity("HabitTags",
                    l => l.HasOne(typeof(Habit)).WithMany().HasForeignKey(nameof(Habit) + nameof(Habit.Id)).OnDelete(DeleteBehavior.Cascade),
                    r => r.HasOne(typeof(Tag)).WithMany().HasForeignKey(nameof(Tag) + nameof(Tag.Id)).OnDelete(DeleteBehavior.Cascade));
        });

        modelBuilder.Entity<PushSubscription>(entity =>
        {
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => s.Endpoint).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SentReminder>(entity =>
        {
            entity.HasIndex(r => new { r.HabitId, r.Date, r.MinutesBefore }).IsUnique();
        });

        modelBuilder.Entity<SentSlipAlert>(entity =>
        {
            entity.HasIndex(a => new { a.HabitId, a.WeekStart }).IsUnique();
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasIndex(n => new { n.UserId, n.IsRead });
            entity.HasIndex(n => n.Url).HasFilter("\"Url\" IS NOT NULL");
        });

        ConfigureGoalEntity(modelBuilder, encConverter, nullableEncConverter);
        ConfigureGoalProgressLogEntity(modelBuilder, nullableEncConverter);

        modelBuilder.Entity<Referral>(entity =>
        {
            entity.HasIndex(r => r.ReferrerId);
            entity.HasIndex(r => r.ReferredUserId).IsUnique();
        });

        modelBuilder.Entity<UserAchievement>(entity =>
        {
            entity.HasIndex(ua => new { ua.UserId, ua.AchievementId }).IsUnique();
            entity.HasIndex(ua => ua.UserId);
            entity.Property(ua => ua.AchievementId).HasMaxLength(50);
        });

        modelBuilder.Entity<StreakFreeze>(entity =>
        {
            entity.HasIndex(sf => new { sf.UserId, sf.UsedOnDate }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(sf => sf.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasIndex(s => s.TokenHash).IsUnique();
            entity.HasIndex(s => s.UserId);
            entity.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(s => s.TokenHash).HasMaxLength(128);
        });

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasIndex(k => k.KeyPrefix);
            entity.HasIndex(k => k.UserId);
            entity.HasOne<User>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(k => k.Name).HasMaxLength(50);
            entity.Property(k => k.KeyPrefix).HasMaxLength(12);
            entity.Property(k => k.Scopes)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql(EmptyJsonArraySql)
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<string>());
        });

        modelBuilder.Entity<PendingAgentOperationState>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.CapabilityId });
            entity.HasIndex(item => new { item.UserId, item.OperationFingerprint });
            entity.Property(item => item.CapabilityId).HasMaxLength(100);
            entity.Property(item => item.OperationId).HasMaxLength(100);
            entity.Property(item => item.ArgumentsJson).HasColumnType(JsonbColumnType);
            entity.Property(item => item.DisplayName).HasMaxLength(200);
            entity.Property(item => item.Summary).HasMaxLength(500);
            entity.Property(item => item.OperationFingerprint).HasMaxLength(256);
            entity.Property(item => item.ConfirmationTokenHash).HasMaxLength(64);
            entity.Property(item => item.Surface).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.RiskClass).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ConfirmationRequirement).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<AgentStepUpChallengeState>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.PendingOperationId, item.CreatedAtUtc });
            entity.Property(item => item.CodeHash).HasMaxLength(64);
        });

        modelBuilder.Entity<AgentAuditLog>(entity =>
        {
            entity.HasIndex(item => new { item.UserId, item.CreatedAtUtc });
            entity.HasIndex(item => new { item.CapabilityId, item.CreatedAtUtc });
            entity.Property(item => item.CapabilityId).HasMaxLength(100);
            entity.Property(item => item.SourceName).HasMaxLength(100);
            entity.Property(item => item.CorrelationId).HasMaxLength(100);
            entity.Property(item => item.TargetId).HasMaxLength(100);
            entity.Property(item => item.TargetName).HasMaxLength(200);
            entity.Property(item => item.RedactedArguments).HasMaxLength(4000);
            entity.Property(item => item.Error).HasMaxLength(500);
            entity.Property(item => item.ShadowReason).HasMaxLength(500);
            entity.Property(item => item.Surface).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.AuthMethod).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.RiskClass).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.PolicyDecision).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.OutcomeStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.ShadowPolicyDecision).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<DistributedRateLimitBucket>(entity =>
        {
            entity.HasIndex(item => new { item.PolicyName, item.PartitionKey, item.WindowStartUtc }).IsUnique();
            entity.Property(item => item.PolicyName).HasMaxLength(64);
            entity.Property(item => item.PartitionKey).HasMaxLength(256);
        });

        modelBuilder.Entity<AppConfig>(entity =>
        {
            entity.HasKey(c => c.Key);
            entity.Property(c => c.Key).HasMaxLength(100);
            entity.Property(c => c.Value).HasMaxLength(500).IsRequired();
            entity.Property(c => c.Description).HasMaxLength(500);

            entity.HasData(
                AppConfig.Create("MaxUserFacts", "50", "Maximum number of facts the AI can remember per user"),
                AppConfig.Create("MaxHabitDepth", "5", "Maximum nesting depth for sub-habits"),
                AppConfig.Create("MaxTagsPerHabit", "5", "Maximum number of tags per habit"),
                AppConfig.Create("ReferralRewardDays", "10", "Days of Pro added per successful referral"),
                AppConfig.Create("MaxReferrals", "10", "Maximum successful referrals per user"));
        });

        modelBuilder.Entity<ChecklistTemplate>(entity =>
        {
            entity.HasIndex(ct => ct.UserId);
            entity.HasOne<User>().WithMany().HasForeignKey(ct => ct.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(ct => ct.Name).HasMaxLength(100);
            entity.Property(ct => ct.Items)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>())
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql("'[]'::jsonb")
                .Metadata.SetValueComparer(
                    new ValueComparer<IReadOnlyList<string>>(
                        (l1, l2) => JsonSerializer.Serialize(l1, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(l2, (JsonSerializerOptions?)null),
                        c => JsonSerializer.Serialize(c, (JsonSerializerOptions?)null).GetHashCode(),
                        c => JsonSerializer.Deserialize<List<string>>(JsonSerializer.Serialize(c, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!));
        });

        modelBuilder.Entity<AppFeatureFlag>(entity =>
        {
            entity.HasKey(f => f.Key);
            entity.Property(f => f.Key).HasMaxLength(100);
            entity.Property(f => f.Description).HasMaxLength(500);
            entity.Property(f => f.PlanRequirement).HasMaxLength(50);

            // Seed data uses anonymous types for deterministic values (required by EF Core HasData).
            entity.HasData(
                new { Key = "offline_mode", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Enable offline mode with background sync", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "ai_chat", Enabled = true, PlanRequirement = (string?)"Free", Description = (string?)"AI chat assistant", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "ai_summary", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"AI daily summary", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "ai_retrospective", Enabled = true, PlanRequirement = (string?)"YearlyPro", Description = (string?)"AI retrospective analysis", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "sub_habits", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Sub-habit nesting", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "goal_tracking", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Goal tracking with progress", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "push_notifications", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Push notification reminders", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "scheduled_reminders", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Custom scheduled reminders", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "slip_alerts", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Slip detection alerts", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "checklist_templates", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Reusable checklist templates", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "habit_duplication", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Duplicate habits", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "bulk_operations", Enabled = true, PlanRequirement = (string?)null, Description = (string?)"Bulk create/delete/log habits", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "calendar_integration", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Google Calendar integration", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
                new { Key = "api_keys", Enabled = true, PlanRequirement = (string?)"Pro", Description = (string?)"Personal API keys", UpdatedAtUtc = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) });
        });

        modelBuilder.Entity<ContentBlock>(entity =>
        {
            entity.HasIndex(cb => new { cb.Key, cb.Locale }).IsUnique();
            entity.Property(cb => cb.Key).HasMaxLength(100);
            entity.Property(cb => cb.Locale).HasMaxLength(10);
            entity.Property(cb => cb.Category).HasMaxLength(50);
        });
    }

    private static void ConfigureUserEntity(ModelBuilder modelBuilder, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasIndex(u => u.ReferralCode).IsUnique().HasFilter("\"ReferralCode\" IS NOT NULL");

            entity.Property(u => u.GoogleCalendarAutoSyncStatus)
                .HasConversion<string>()
                .HasMaxLength(32);

            entity.Property(u => u.GoogleCalendarLastSyncError).HasMaxLength(500);

            entity.HasIndex(u => new { u.GoogleCalendarAutoSyncEnabled, u.GoogleCalendarLastSyncedAt })
                .HasFilter("\"GoogleCalendarAutoSyncEnabled\" = TRUE");

            if (nullableEncConverter is null)
                return;

            entity.Property(u => u.GoogleAccessToken).HasConversion(nullableEncConverter).HasColumnType("text");
            entity.Property(u => u.GoogleRefreshToken).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureGoogleCalendarSyncSuggestionEntity(
        ModelBuilder modelBuilder,
        EncryptionValueConverter? encConverter,
        NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<GoogleCalendarSyncSuggestion>(entity =>
        {
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => new { s.UserId, s.GoogleEventId }).IsUnique();
            entity.HasIndex(s => new { s.UserId, s.DismissedAtUtc, s.ImportedAtUtc });

            entity.Property(s => s.GoogleEventId).HasMaxLength(256).IsRequired();

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            if (encConverter is null || nullableEncConverter is null)
                return;

            entity.Property(s => s.Title).HasConversion(encConverter).HasColumnType("text");
            entity.Property(s => s.RawEventJson).HasConversion(encConverter).HasColumnType("text");
        });
    }

    private static void ConfigureHabitEntity(
        ModelBuilder modelBuilder,
        bool usePostgresArrayColumns,
        EncryptionValueConverter? encConverter,
        NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<Habit>(entity =>
        {
            entity.HasIndex(h => h.UserId);
            entity.HasIndex(h => new { h.UserId, h.IsDeleted });
            entity.HasQueryFilter(h => !h.IsDeleted);

            entity.Property(h => h.GoogleEventId).HasMaxLength(256);
            entity.HasIndex(h => new { h.UserId, h.GoogleEventId })
                .HasFilter("\"GoogleEventId\" IS NOT NULL AND \"IsDeleted\" = FALSE")
                .IsUnique();

            entity.HasMany(h => h.Logs)
                .WithOne()
                .HasForeignKey(l => l.HabitId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(h => h.Children)
                .WithOne()
                .HasForeignKey(h => h.ParentHabitId)
                .OnDelete(DeleteBehavior.Cascade);

            ConfigureHabitDaysProperty(entity, usePostgresArrayColumns);

            entity.Property(h => h.ChecklistItems)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<ChecklistItem>()))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql("'[]'::jsonb")
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<ChecklistItem>());

            entity.Property(h => h.ReminderTimes)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<int> { 15 }))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql("'[15]'::jsonb")
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<int>());

            entity.Property(h => h.ScheduledReminders)
                .HasConversion(
                    v => SerializeJson(v),
                    v => DeserializeJson(v, new List<ScheduledReminderTime>()))
                .HasColumnType(JsonbColumnType)
                .HasDefaultValueSql("'[]'::jsonb")
                .Metadata.SetValueComparer(CreateReadOnlyListComparer<ScheduledReminderTime>());

            if (encConverter is null || nullableEncConverter is null)
                return;

            entity.Property(h => h.Title).HasConversion(encConverter).HasColumnType("text");
            entity.Property(h => h.Description).HasConversion(nullableEncConverter).HasColumnType("text");
            entity.Property(h => h.Emoji).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureHabitDaysProperty(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Habit> entity,
        bool usePostgresArrayColumns)
    {
        var property = entity.Property(h => h.Days);

        if (usePostgresArrayColumns)
        {
            property.HasConversion(
                    v => v.ToList(),
                    v => v.ToList())
                .HasColumnType("text[]");
        }
        else
        {
            property.HasConversion(
                    v => SerializeDays(v),
                    v => DeserializeDays(v))
                .HasColumnType("text");
        }

        property.Metadata.SetValueComparer(CreateDayOfWeekCollectionComparer());
    }

    private static void ConfigureHabitLogEntity(ModelBuilder modelBuilder, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<HabitLog>(entity =>
        {
            entity.HasIndex(l => new { l.HabitId, l.Date });

            if (nullableEncConverter is null)
                return;

            entity.Property(l => l.Note).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureUserFactEntity(ModelBuilder modelBuilder, EncryptionValueConverter? encConverter)
    {
        modelBuilder.Entity<UserFact>(entity =>
        {
            entity.HasIndex(f => new { f.UserId, f.IsDeleted });
            entity.HasQueryFilter(f => !f.IsDeleted);

            if (encConverter is null)
                return;

            entity.Property(f => f.FactText).HasConversion(encConverter).HasColumnType("text");
        });
    }

    private static void ConfigureGoalEntity(
        ModelBuilder modelBuilder,
        EncryptionValueConverter? encConverter,
        NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<Goal>(entity =>
        {
            entity.HasIndex(g => g.UserId);
            entity.HasIndex(g => new { g.UserId, g.IsDeleted });
            entity.HasQueryFilter(g => !g.IsDeleted);

            entity.HasMany(g => g.ProgressLogs)
                .WithOne()
                .HasForeignKey(l => l.GoalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(g => g.Habits)
                .WithMany(h => h.Goals)
                .UsingEntity("HabitGoals",
                    l => l.HasOne(typeof(Habit)).WithMany().HasForeignKey(nameof(Habit) + nameof(Habit.Id)).OnDelete(DeleteBehavior.Cascade),
                    r => r.HasOne(typeof(Goal)).WithMany().HasForeignKey(nameof(Goal) + nameof(Goal.Id)).OnDelete(DeleteBehavior.Cascade));

            if (encConverter is null || nullableEncConverter is null)
                return;

            entity.Property(g => g.Title).HasConversion(encConverter).HasColumnType("text");
            entity.Property(g => g.Description).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    private static void ConfigureGoalProgressLogEntity(ModelBuilder modelBuilder, NullableEncryptionValueConverter? nullableEncConverter)
    {
        modelBuilder.Entity<GoalProgressLog>(entity =>
        {
            entity.HasIndex(l => l.GoalId);

            if (nullableEncConverter is null)
                return;

            entity.Property(l => l.Note).HasConversion(nullableEncConverter).HasColumnType("text");
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<ITimestamped>()
            .Where(e => e.State is EntityState.Modified or EntityState.Added))
        {
            entry.Entity.UpdatedAtUtc = now;
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    private static string SerializeDays(ICollection<System.DayOfWeek> days)
    {
        return JsonSerializer.Serialize(days.Select(day => (int)day).ToList(), (JsonSerializerOptions?)null);
    }

    private static string SerializeJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, (JsonSerializerOptions?)null);
    }

    private static List<T> DeserializeJson<T>(string value, List<T> fallback)
    {
        return JsonSerializer.Deserialize<List<T>>(value, (JsonSerializerOptions?)null) ?? fallback;
    }

    private static List<System.DayOfWeek> DeserializeDays(string value)
    {
        var days = JsonSerializer.Deserialize<List<int>>(value, (JsonSerializerOptions?)null);
        return days?.Select(day => (System.DayOfWeek)day).ToList() ?? new List<System.DayOfWeek>();
    }

    private static ValueComparer<ICollection<System.DayOfWeek>> CreateDayOfWeekCollectionComparer()
    {
        return new ValueComparer<ICollection<System.DayOfWeek>>(
            (left, right) => left!.SequenceEqual(right!),
            collection => collection.Aggregate(0, (hash, day) => HashCode.Combine(hash, day.GetHashCode())),
            collection => collection.ToList());
    }

    private static ValueComparer<IReadOnlyList<T>> CreateReadOnlyListComparer<T>()
    {
        return new ValueComparer<IReadOnlyList<T>>(
            (left, right) => SerializeJson(left) == SerializeJson(right),
            collection => SerializeJson(collection).GetHashCode(),
            collection => DeserializeJson(SerializeJson(collection), new List<T>()));
    }
}
