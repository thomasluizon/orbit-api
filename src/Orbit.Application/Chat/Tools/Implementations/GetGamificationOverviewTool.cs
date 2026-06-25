using System.Text.Json;
using MediatR;
using Orbit.Application.Gamification.Queries;

namespace Orbit.Application.Chat.Tools.Implementations;

public class GetGamificationOverviewTool(IMediator mediator) : IAiTool
{
    public string Name => "get_gamification_overview";
    public string Description => "Read the user's gamification profile, achievements, and streak information.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            include_profile = new { type = JsonSchemaTypes.Boolean },
            include_achievements = new { type = JsonSchemaTypes.Boolean },
            include_streak = new { type = JsonSchemaTypes.Boolean }
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var includeProfile = JsonArgumentParser.GetOptionalBool(args, "include_profile") ?? true;
        var includeAchievements = JsonArgumentParser.GetOptionalBool(args, "include_achievements") ?? true;
        var includeStreak = JsonArgumentParser.GetOptionalBool(args, "include_streak") ?? true;

        object? profile = null;
        object? achievements = null;
        object? streak = null;

        if (includeProfile)
        {
            var profileResult = await mediator.Send(new GetGamificationProfileQuery(userId), ct);
            if (profileResult.IsFailure) return ToolResult.FromFailure(profileResult);
            profile = profileResult.Value;
        }

        if (includeAchievements)
        {
            var achievementsResult = await mediator.Send(new GetAchievementsQuery(userId), ct);
            if (achievementsResult.IsFailure) return ToolResult.FromFailure(achievementsResult);
            achievements = achievementsResult.Value;
        }

        if (includeStreak)
        {
            var streakResult = await mediator.Send(new GetStreakInfoQuery(userId), ct);
            if (streakResult.IsFailure) return ToolResult.FromFailure(streakResult);
            streak = streakResult.Value;
        }

        return new ToolResult(true, Payload: new { profile, achievements, streak });
    }
}
