using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Models;

namespace Orbit.Application.Habits.Services;

/// <summary>
/// Owns the per-user cache entry for a habit setup suggestion: its key and its lifetime.
/// The suggest-setup and phrase-reader handlers both read and write the entry through this type,
/// so a suggestion for one title serves both and the two writers cannot drift apart.
/// </summary>
internal static class HabitSetupSuggestionCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    public static bool TryGet(
        IMemoryCache cache,
        Guid userId,
        string title,
        string language,
        [NotNullWhen(true)] out HabitSetupSuggestion? suggestion) =>
        cache.TryGetValue(BuildKey(userId, title, language), out suggestion) && suggestion is not null;

    public static void Set(
        IMemoryCache cache,
        Guid userId,
        string title,
        string language,
        HabitSetupSuggestion suggestion) =>
        cache.Set(BuildKey(userId, title, language), suggestion, Ttl);

    private static string BuildKey(Guid userId, string title, string language)
    {
        var normalizedTitle = title.Trim().ToLowerInvariant();
        var titleHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedTitle)));
        return $"suggest-setup:{userId}:{titleHash}:{language.ToLowerInvariant()}";
    }
}
