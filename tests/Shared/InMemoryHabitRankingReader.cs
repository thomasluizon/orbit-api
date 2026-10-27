using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Tests.Shared;

public sealed class InMemoryHabitRankingReader(IEnumerable<Habit> habits) : IHabitRankingReader
{
    public Task<HabitRankingPage> ReadAsync(HabitRankingDefinition query, int offset,
        CancellationToken cancellationToken = default)
    {
        var eligible = habits.Where(h => h.UserId == query.UserId && !h.IsDeleted && !h.IsBadHabit
            && h.Schedule is HabitSchedule.Recurring or HabitSchedule.Flexible
            && (query.HabitIds.Count == 0 || query.HabitIds.Contains(h.Id))).ToArray();
        var ranked = eligible.Select(h => new HabitRankingReadRow(h.Id, h.Title, h.Emoji, h.ParentHabitId,
                h.Logs.Count(l => !l.IsDeleted && !l.IsSlip && l.Value > 0
                    && l.Date >= query.DateFrom && l.Date < query.DateToExclusive)))
            .Where(h => h.CompletedCount > 0).OrderByDescending(h => h.CompletedCount)
            .ThenBy(h => h.HabitId.ToString("D"), StringComparer.Ordinal).ToArray();
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ranked))));
        return Task.FromResult(new HabitRankingPage(ranked.Skip(offset).Take(query.PageSize).ToArray(),
            eligible.Length, ranked.Length, fingerprint));
    }
}
