using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Persistence;

public class OrbitDbContext : DbContext
{
    private readonly IEncryptionService? _encryptionService;

    public OrbitDbContext(DbContextOptions<OrbitDbContext> options, IEncryptionService? encryptionService = null)
        : base(options)
    {
        _encryptionService = encryptionService;
    }

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
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // --- Encryption Value Converters ---
        EncryptionValueConverter? encConverter = null;
        NullableEncryptionValueConverter? nullableEncConverter = null;

        if (_encryptionService is not null)
        {
            encConverter = new EncryptionValueConverter(_encryptionService);
            nullableEncConverter = new NullableEncryptionValueConverter(_encryptionService);
        }

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasIndex(u => u.ReferralCode).IsUnique().HasFilter("\"ReferralCode\" IS NOT NULL");

            if (nullableEncConverter is not null)
            {
                entity.Property(u => u.GoogleAccessToken).HasConversion(nullableEncConverter).HasColumnType("text");
                entity.Property(u => u.GoogleRefreshToken).HasConversion(nullableEncConverter).HasColumnType("text");
            }
        });

        modelBuilder.Entity<Habit>(entity =>
        {
            entity.HasIndex(h => h.UserId);

            entity.HasMany(h => h.Logs)
                .WithOne()
                .HasForeignKey(l => l.HabitId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(h => h.Children)
                .WithOne()
                .HasForeignKey(h => h.ParentHabitId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(h => h.Days)
                .HasConversion(
                    v => v.ToList(),
                    v => v.ToList())
                .HasColumnType("text[]")
                .Metadata.SetValueComparer(
                    new ValueComparer<ICollection<System.DayOfWeek>>(
                        (l1, l2) => l1!.SequenceEqual(l2!),
                        c => c.Aggregate(0, (a, v) => HashCode.Combine(a, v.GetHashCode())),
                        c => c.ToList()));

            entity.Property(h => h.ChecklistItems)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<ChecklistItem>>(v, (JsonSerializerOptions?)null) ?? new List<ChecklistItem>())
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb")
                .Metadata.SetValueComparer(
                    new ValueComparer<IReadOnlyList<ChecklistItem>>(
                        (l1, l2) => JsonSerializer.Serialize(l1, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(l2, (JsonSerializerOptions?)null),
                        c => JsonSerializer.Serialize(c, (JsonSerializerOptions?)null).GetHashCode(),
                        c => JsonSerializer.Deserialize<List<ChecklistItem>>(JsonSerializer.Serialize(c, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!));

            entity.Property(h => h.ReminderTimes)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<int>>(v, (JsonSerializerOptions?)null) ?? new List<int> { 15 })
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[15]'::jsonb")
                .Metadata.SetValueComparer(
                    new ValueComparer<IReadOnlyList<int>>(
                        (l1, l2) => JsonSerializer.Serialize(l1, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(l2, (JsonSerializerOptions?)null),
                        c => JsonSerializer.Serialize(c, (JsonSerializerOptions?)null).GetHashCode(),
                        c => JsonSerializer.Deserialize<List<int>>(JsonSerializer.Serialize(c, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!));

            entity.Property(h => h.ScheduledReminders)
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<List<ScheduledReminderTime>>(v, (JsonSerializerOptions?)null) ?? new List<ScheduledReminderTime>())
                .HasColumnType("jsonb")
                .HasDefaultValueSql("'[]'::jsonb")
                .Metadata.SetValueComparer(
                    new ValueComparer<IReadOnlyList<ScheduledReminderTime>>(
                        (l1, l2) => JsonSerializer.Serialize(l1, (JsonSerializerOptions?)null) == JsonSerializer.Serialize(l2, (JsonSerializerOptions?)null),
                        c => JsonSerializer.Serialize(c, (JsonSerializerOptions?)null).GetHashCode(),
                        c => JsonSerializer.Deserialize<List<ScheduledReminderTime>>(JsonSerializer.Serialize(c, (JsonSerializerOptions?)null), (JsonSerializerOptions?)null)!));

            if (encConverter is not null && nullableEncConverter is not null)
            {
                entity.Property(h => h.Title).HasConversion(encConverter).HasColumnType("text");
                entity.Property(h => h.Description).HasConversion(nullableEncConverter).HasColumnType("text");
            }
        });

        modelBuilder.Entity<HabitLog>(entity =>
        {
            entity.HasIndex(l => new { l.HabitId, l.Date });

            if (nullableEncConverter is not null)
            {
                entity.Property(l => l.Note).HasConversion(nullableEncConverter).HasColumnType("text");
            }
        });

        modelBuilder.Entity<UserFact>(entity =>
        {
            entity.HasIndex(f => new { f.UserId, f.IsDeleted });
            entity.HasQueryFilter(f => !f.IsDeleted);

            if (encConverter is not null)
            {
                entity.Property(f => f.FactText).HasConversion(encConverter).HasColumnType("text");
            }
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.Name }).IsUnique();

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

        modelBuilder.Entity<Goal>(entity =>
        {
            entity.HasIndex(g => g.UserId);

            entity.HasMany(g => g.ProgressLogs)
                .WithOne()
                .HasForeignKey(l => l.GoalId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(g => g.Habits)
                .WithMany(h => h.Goals)
                .UsingEntity("HabitGoals",
                    l => l.HasOne(typeof(Habit)).WithMany().HasForeignKey(nameof(Habit) + nameof(Habit.Id)).OnDelete(DeleteBehavior.Cascade),
                    r => r.HasOne(typeof(Goal)).WithMany().HasForeignKey(nameof(Goal) + nameof(Goal.Id)).OnDelete(DeleteBehavior.Cascade));

            if (encConverter is not null && nullableEncConverter is not null)
            {
                entity.Property(g => g.Title).HasConversion(encConverter).HasColumnType("text");
                entity.Property(g => g.Description).HasConversion(nullableEncConverter).HasColumnType("text");
            }
        });

        modelBuilder.Entity<GoalProgressLog>(entity =>
        {
            entity.HasIndex(l => l.GoalId);

            if (nullableEncConverter is not null)
            {
                entity.Property(l => l.Note).HasConversion(nullableEncConverter).HasColumnType("text");
            }
        });

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

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasIndex(k => k.KeyPrefix);
            entity.HasIndex(k => k.UserId);
            entity.HasOne<User>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.Property(k => k.Name).HasMaxLength(50);
            entity.Property(k => k.KeyPrefix).HasMaxLength(12);
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
    }
}
