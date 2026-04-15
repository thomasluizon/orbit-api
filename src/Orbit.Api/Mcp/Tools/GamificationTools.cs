using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Gamification.Commands;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class GamificationTools(IMediator mediator)
{
    [McpServerTool(Name = "get_gamification_profile"), Description("Get the user's gamification profile: XP, level, achievements, and streak info. Requires Pro subscription.")]
    public async Task<string> GetGamificationProfile(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetGamificationProfileQuery(userId);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var p = result.Value;
        return $"Level: {p.Level} ({p.LevelTitle})\n" +
               $"Total XP: {p.TotalXp}\n" +
               $"XP Progress: {p.XpForCurrentLevel}/{p.XpForNextLevel}" +
               (p.XpToNextLevel is not null ? $" ({p.XpToNextLevel} to next level)" : "") + "\n" +
               $"Achievements: {p.AchievementsEarned}/{p.AchievementsTotal}\n" +
               $"Current Streak: {p.CurrentStreak} days\n" +
               $"Longest Streak: {p.LongestStreak} days\n" +
               (p.LastActiveDate is not null ? $"Last Active: {p.LastActiveDate}" : "");
    }

    [McpServerTool(Name = "get_achievements"), Description("Get all achievements with their earned status. Requires Pro subscription.")]
    public async Task<string> GetAchievements(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetAchievementsQuery(userId);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var achievements = result.Value.Achievements;
        if (achievements.Count == 0)
            return "No achievements available.";

        var earned = achievements.Where(a => a.IsEarned).ToList();
        var locked = achievements.Where(a => !a.IsEarned).ToList();

        var lines = new List<string>();
        if (earned.Count > 0)
        {
            lines.Add($"Earned ({earned.Count}):");
            lines.AddRange(earned.Select(a =>
                $"  - {a.Name} ({a.Rarity}) | {a.XpReward} XP | {a.Description}"));
        }
        if (locked.Count > 0)
        {
            lines.Add($"Locked ({locked.Count}):");
            lines.AddRange(locked.Select(a =>
                $"  - {a.Name} ({a.Rarity}) | {a.XpReward} XP | {a.Description}"));
        }

        return string.Join("\n", lines);
    }

    [McpServerTool(Name = "get_streak_info"), Description("Get detailed streak information including freeze availability.")]
    public async Task<string> GetStreakInfo(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var query = new GetStreakInfoQuery(userId);
        var result = await mediator.Send(query, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var s = result.Value;
        return $"Current Streak: {s.CurrentStreak} days\n" +
               $"Longest Streak: {s.LongestStreak} days\n" +
               (s.LastActiveDate is not null ? $"Last Active: {s.LastActiveDate}\n" : "") +
               $"Frozen Today: {(s.IsFrozenToday ? "Yes" : "No")}\n" +
               $"Freezes Available: {s.FreezesAvailable}/{s.MaxFreezesPerMonth} this month\n" +
               $"Freezes Used: {s.FreezesUsedThisMonth}\n" +
               (s.RecentFreezeDates.Count > 0 ? $"Recent freeze dates: {string.Join(", ", s.RecentFreezeDates)}" : "");
    }

    [McpServerTool(Name = "activate_streak_freeze"), Description("Activate a streak freeze to protect the current streak. Limited to 2 per month.")]
    public async Task<string> ActivateStreakFreeze(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(user);
        var command = new ActivateStreakFreezeCommand(userId);
        var result = await mediator.Send(command, cancellationToken);

        if (result.IsFailure)
            return $"Error: {result.Error}";

        var r = result.Value;
        return $"Streak freeze activated for {r.FrozenDate}\n" +
               $"Current streak preserved: {r.CurrentStreak} days\n" +
               $"Freezes remaining this month: {r.FreezesRemainingThisMonth}";
    }

    private static Guid GetUserId(ClaimsPrincipal user)
    {
        var claim = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("User ID not found in token");
        if (!Guid.TryParse(claim, out var userId))
            throw new UnauthorizedAccessException("User ID claim is not a valid GUID");
        return userId;
    }
}
