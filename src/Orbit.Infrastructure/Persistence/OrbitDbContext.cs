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

        modelBuilder.Entity<AppConfig>(entity =>
        {
            entity.HasKey(c => c.Key);
            entity.Property(c => c.Key).HasMaxLength(100);
            entity.Property(c => c.Value).HasMaxLength(500).IsRequired();
            entity.Property(c => c.Description).HasMaxLength(500);

            entity.HasData(
                AppConfig.Create("MaxUserFacts", "50", "Maximum number of facts the AI can remember per user"));
        });
    }
}
