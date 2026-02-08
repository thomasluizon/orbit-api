using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

public class OrbitDbContext(DbContextOptions<OrbitDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<HabitLog> HabitLogs => Set<HabitLog>();
    public DbSet<Tag> Tags => Set<Tag>();

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

        modelBuilder.Entity<Tag>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.Name }).IsUnique();
        });

        modelBuilder.Entity<Habit>()
            .HasMany(h => h.Tags)
            .WithMany(t => t.Habits)
            .UsingEntity<HabitTag>(
                r => r.HasOne<Tag>().WithMany().HasForeignKey(ht => ht.TagId)
                    .OnDelete(DeleteBehavior.Cascade),
                l => l.HasOne<Habit>().WithMany().HasForeignKey(ht => ht.HabitId)
                    .OnDelete(DeleteBehavior.Cascade),
                j => j.HasKey(ht => new { ht.HabitId, ht.TagId }));
    }
}
