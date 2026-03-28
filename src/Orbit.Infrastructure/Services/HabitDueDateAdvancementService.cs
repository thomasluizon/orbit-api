using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Services;

public class HabitDueDateAdvancementService(
    IServiceScopeFactory scopeFactory,
    ILogger<HabitDueDateAdvancementService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AdvanceStaleDueDates(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error in habit due date advancement");
            }

            await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
        }
    }

    private async Task AdvanceStaleDueDates(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrbitDbContext>();

        // Conservative cutoff: UTC today - 1 day to avoid timezone false positives
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        var habits = await dbContext.Habits
            .Where(h => !h.IsCompleted
                && h.FrequencyUnit != null
                && !h.IsFlexible
                && h.DueDate < cutoff)
            .ToListAsync(ct);

        if (habits.Count == 0) return;

        var userIds = habits.Select(h => h.UserId).Distinct().ToList();
        var users = await dbContext.Users
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var advanced = 0;
        foreach (var habit in habits)
        {
            if (!users.TryGetValue(habit.UserId, out var user)) continue;

            var tz = user.TimeZone is not null
                ? TimeZoneInfo.FindSystemTimeZoneById(user.TimeZone)
                : TimeZoneInfo.Utc;
            var userNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var userToday = DateOnly.FromDateTime(userNow);

            if (habit.DueDate >= userToday) continue;

            habit.CatchUpDueDate(userToday);
            advanced++;
        }

        if (advanced > 0)
        {
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation("Advanced DueDate for {Count} recurring habits", advanced);
        }
    }
}
