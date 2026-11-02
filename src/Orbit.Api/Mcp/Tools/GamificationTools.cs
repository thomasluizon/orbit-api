using System.ComponentModel;
using System.Security.Claims;
using MediatR;
using ModelContextProtocol.Server;
using Orbit.Application.Gamification.Queries;
using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Orbit.Api.Mcp.Tools;

[McpServerToolType]
public class GamificationTools(IMediator mediator)
{
    [McpServerTool(Name = "get_gamification_profile"), Description("Get the user's gamification profile: XP, level, achievements, and streak info. Requires Pro access or the gamification_free_tier flag.")]
    public async Task<CallToolResult> GetGamificationProfile(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var query = new GetGamificationProfileQuery(userId);
            var result = await mediator.Send(query, cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var p = result.Value;
            return McpToolHelpers.Text($"Level: {p.Level} ({p.LevelTitle})\n" + $"Total XP: {p.TotalXp}\n" + $"XP Progress: {p.XpForCurrentLevel}/{p.XpForNextLevel}" + (p.XpToNextLevel is not null ? $" ({p.XpToNextLevel} to next level)" : "") + "\n" + $"Achievements: {p.AchievementsEarned}/{p.AchievementsTotal}\n" + $"Current Streak: {p.CurrentStreak} days\n" + $"Longest Streak: {p.LongestStreak} days\n" + (p.LastActiveDate is not null ? $"Last Active: {p.LastActiveDate}" : ""));
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "get_achievements"), Description("Get all achievements with their earned status. Requires Pro access or the gamification_free_tier flag.")]
    public async Task<CallToolResult> GetAchievements(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var query = new GetAchievementsQuery(userId);
            var result = await mediator.Send(query, cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var achievements = result.Value.Achievements;
            if (achievements.Count == 0)
                return McpToolHelpers.Text("No achievements available.");
            var earned = achievements.Where(a => a.IsEarned).ToList();
            var locked = achievements.Where(a => !a.IsEarned).ToList();
            var lines = new List<string>();
            if (earned.Count > 0)
            {
                lines.Add($"Earned ({earned.Count}):");
                lines.AddRange(earned.Select(a => $"  - {a.Name} ({a.Rarity}) | {a.XpReward} XP | {a.Description}"));
            }

            if (locked.Count > 0)
            {
                lines.Add($"Locked ({locked.Count}):");
                lines.AddRange(locked.Select(a => $"  - {a.Name} ({a.Rarity}) | {a.XpReward} XP | {a.Description}"));
            }

            return McpToolHelpers.Text(string.Join("\n", lines));
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }

    [McpServerTool(Name = "get_streak_info"), Description("Get detailed streak information including freeze availability. Requires Pro access or the gamification_free_tier flag.")]
    public async Task<CallToolResult> GetStreakInfo(
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var userId = McpToolHelpers.GetUserId(user);
            var query = new GetStreakInfoQuery(userId);
            var result = await mediator.Send(query, cancellationToken);
            if (result.IsFailure)
                return McpToolHelpers.Error($"Error: {result.Error}");
            var s = result.Value;
            return McpToolHelpers.Text($"Current Streak: {s.CurrentStreak} days\n" + $"Longest Streak: {s.LongestStreak} days\n" + (s.LastActiveDate is not null ? $"Last Active: {s.LastActiveDate}\n" : "") + $"Frozen Today: {(s.IsFrozenToday ? "Yes" : "No")}\n" + $"Freezes Available: {s.FreezesAvailable}/{s.MaxFreezesPerMonth} this month\n" + $"Freezes Used: {s.FreezesUsedThisMonth}\n" + (s.RecentFreezeDates.Count > 0 ? $"Recent freeze dates: {string.Join(", ", s.RecentFreezeDates)}" : ""));
        }
        catch (ArgumentException ex)
        {
            return McpToolHelpers.Error(ex.Message);
        }
        catch (JsonException ex)
        {
            return McpToolHelpers.Error("Invalid JSON input: " + ex.Message);
        }
    }
}
