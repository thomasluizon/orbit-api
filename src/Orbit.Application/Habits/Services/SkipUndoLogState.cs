using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Habits.Services;

public static class SkipUndoLogState
{
    public static async Task<string> ReadAsync(
        IGenericRepository<HabitLog> logs, Habit habit, CancellationToken cancellationToken)
    {
        var states = await logs.ProjectAsync(
            log => log.HabitId == habit.Id,
            query => query.IgnoreQueryFilters().OrderBy(log => log.Id)
                .Select(log => new LogState(log.Id, log.UpdatedAtUtc, log.IsDeleted)),
            cancellationToken);
        var serialized = string.Join('|', states.Select(state => string.Create(
            CultureInfo.InvariantCulture, $"{state.Id}:{state.UpdatedAtUtc.Ticks / 10}:{state.IsDeleted}")));
        var tags = string.Join(',', habit.Tags.Select(tag => tag.Id).Order());
        var goals = string.Join(',', habit.Goals.Select(goal => goal.Id).Order());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{serialized}|tags:{tags}|goals:{goals}")));
    }

    public sealed record LogState(Guid Id, DateTime UpdatedAtUtc, bool IsDeleted);
}
