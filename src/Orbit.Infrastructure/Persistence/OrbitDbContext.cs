using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

public class OrbitDbContext(DbContextOptions<OrbitDbContext> options) : DbContext(options)
{
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Habit>(entity =>
        {
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

            entity.HasIndex(h => new { h.UserId, h.IsActive });
        });

        modelBuilder.Entity<HabitLog>(entity =>
        {
            entity.HasIndex(l => new { l.HabitId, l.Date });
        });

        modelBuilder.Entity<UserFact>(entity =>
        {
            entity.HasIndex(f => new { f.UserId, f.IsDeleted });
            entity.HasQueryFilter(f => !f.IsDeleted);
        });

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.Name }).IsUnique();

            entity.HasMany(t => t.Habits)
                .WithMany(h => h.Tags)
                .UsingEntity("HabitTags",
                    l => l.HasOne(typeof(Habit)).WithMany().HasForeignKey("HabitId").OnDelete(DeleteBehavior.Cascade),
                    r => r.HasOne(typeof(Tag)).WithMany().HasForeignKey("TagId").OnDelete(DeleteBehavior.Cascade));
        });

        modelBuilder.Entity<PushSubscription>(entity =>
        {
            entity.HasIndex(s => s.UserId);
            entity.HasIndex(s => s.Endpoint).IsUnique();
        });

        modelBuilder.Entity<SentReminder>(entity =>
        {
            entity.HasIndex(r => new { r.HabitId, r.Date }).IsUnique();
        });

        modelBuilder.Entity<SentSlipAlert>(entity =>
        {
            entity.HasIndex(a => new { a.HabitId, a.WeekStart }).IsUnique();
        });

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasIndex(n => new { n.UserId, n.IsRead });
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
                AppConfig.Create("MaxTagsPerHabit", "5", "Maximum number of tags per habit"));
        });
    }
}
