using System.Text.Json;
using System.Text.Json.Serialization;
using MediatR;
using Orbit.Application.Gamification.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Chat.Tools.Implementations;

public record GamificationOverviewPayload(
    GamificationProfileResponse? Profile,
    AchievementsResponse? Achievements,
    StreakInfoResponse? Streak,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? FirstEligibleDate = null);

public class GetGamificationOverviewTool(
    IMediator mediator,
    IGenericRepository<Habit> habits,
    IUserDateService dates) : IAiTool
{
    public string Name => "get_gamification_overview";
    public string Description => "Read the user's gamification profile, achievements, and streak information.";
    public bool IsReadOnly => true;

    public object GetParameterSchema() => new
    {
        type = JsonSchemaTypes.Object,
        properties = new
        {
            as_of = new { type = JsonSchemaTypes.String },
            include_profile = new
            {
                type = JsonSchemaTypes.Boolean,
                description = "Include XP, level, earned achievement IDs, and profile streak totals. Defaults to true. The achievement catalogue is included here only when include_achievements=false. Set false when these details are not needed."
            },
            include_achievements = new
            {
                type = JsonSchemaTypes.Boolean,
                description = "Include the achievement catalogue with earned status and progress. Defaults to true. Set false unless achievement details are needed."
            },
            include_streak = new
            {
                type = JsonSchemaTypes.Boolean,
                description = "Include current and longest streaks, freezes, and repair availability. Defaults to true. For streak-only questions, use include_streak=true, include_profile=true, and include_achievements=false so streak cards retain level and XP data."
            }
        }
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, Guid userId, CancellationToken ct)
    {
        var includeProfile = JsonArgumentParser.GetOptionalBool(args, "include_profile") ?? true;
        var includeAchievements = JsonArgumentParser.GetOptionalBool(args, "include_achievements") ?? true;
        var includeStreak = JsonArgumentParser.GetOptionalBool(args, "include_streak") ?? true;

        GamificationProfileResponse? profile = null;
        AchievementsResponse? achievements = null;
        StreakInfoResponse? streak = null;
        DateOnly? firstEligibleDate = null;

        if (includeProfile)
        {
            var profileResult = await mediator.Send(new GetGamificationProfileQuery(userId, ReconcileAchievements: false), ct);
            if (profileResult.IsFailure) return ToolResult.FromFailure(profileResult);
            profile = includeAchievements
                ? profileResult.Value with { Achievements = [] }
                : profileResult.Value;
        }

        if (includeAchievements)
        {
            var achievementsResult = await mediator.Send(new GetAchievementsQuery(userId, ReconcileAchievements: false), ct);
            if (achievementsResult.IsFailure) return ToolResult.FromFailure(achievementsResult);
            achievements = achievementsResult.Value;
        }

        if (includeStreak)
        {
            var streakResult = await mediator.Send(new GetStreakInfoQuery(userId, JsonArgumentParser.ParseDateOnly(args, "as_of")), ct);
            if (streakResult.IsFailure) return ToolResult.FromFailure(streakResult);
            streak = streakResult.Value;
            var firstCreatedAtUtc = await habits.ProjectAsync(
                habit => habit.UserId == userId && !habit.IsDeleted && !habit.IsCompleted,
                query => query.OrderBy(habit => habit.CreatedAtUtc).Select(habit => habit.CreatedAtUtc).Take(1), ct);
            if (firstCreatedAtUtc.Count > 0)
            {
                var zone = await dates.GetUserTimeZoneAsync(userId, ct);
                firstEligibleDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(firstCreatedAtUtc[0], zone));
            }
        }

        return new ToolResult(true, Payload: new GamificationOverviewPayload(profile, achievements, streak, firstEligibleDate));
    }
}
