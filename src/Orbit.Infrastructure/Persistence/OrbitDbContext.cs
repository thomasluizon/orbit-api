using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;

namespace Orbit.Infrastructure.Persistence;

public class OrbitDbContext(DbContextOptions<OrbitDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<HabitLog> HabitLogs => Set<HabitLog>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

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

            entity.HasIndex(h => new { h.UserId, h.IsActive });
        });

        modelBuilder.Entity<HabitLog>(entity =>
        {
            entity.HasIndex(l => new { l.HabitId, l.Date });
        });

        modelBuilder.Entity<TaskItem>(entity =>
        {
            entity.HasIndex(t => new { t.UserId, t.Status });
        });
    }
}
