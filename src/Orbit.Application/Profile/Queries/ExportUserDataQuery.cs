using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Connections;
using Orbit.Application.Goals.Services;
using Orbit.Application.Profile.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Queries;

public record ExportUserDataQuery(Guid UserId) : IRequest<Result<UserDataExport>>;

/// <summary>Groups every repository the data-export read touches to keep the handler constructor small.</summary>
public record ExportUserDataRepositories(
    IGenericRepository<User> Users,
    IGenericRepository<Habit> Habits,
    IGenericRepository<HabitLog> HabitLogs,
    IGenericRepository<Goal> Goals,
    IGenericRepository<GoalProgressLog> GoalProgressLogs,
    IGenericRepository<Tag> Tags,
    IGenericRepository<Notification> Notifications,
    IGenericRepository<ChecklistTemplate> ChecklistTemplates,
    IGenericRepository<UserAchievement> UserAchievements,
    IGenericRepository<StreakFreeze> StreakFreezes,
    IGenericRepository<Referral> Referrals,
    IGenericRepository<ApiKey> ApiKeys,
    IGenericRepository<ProactiveItem> ProactiveItems,
    IGenericRepository<NotionConnection> NotionConnections,
    IGenericRepository<NotionTask> NotionTasks,
    IGenericRepository<ConversationTurn> ConversationTurns);

public class ExportUserDataQueryHandler(
    ExportUserDataRepositories repos,
    IUserDateService userDateService,
    IGoalProgressReadSyncer goalProgressReadSyncer)
    : IRequestHandler<ExportUserDataQuery, Result<UserDataExport>>
{
    public async Task<Result<UserDataExport>> Handle(ExportUserDataQuery request, CancellationToken cancellationToken)
    {
        var user = await repos.Users.GetByIdAsync(request.UserId, cancellationToken);

        if (user is null)
            return Result.Failure<UserDataExport>(ErrorMessages.UserNotFound);

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var freshProgressValues = await goalProgressReadSyncer.ComputeFreshValuesAsync(request.UserId, userToday, cancellationToken);

        var habits = await repos.Habits.FindAsync(h => h.UserId == request.UserId, cancellationToken);
        var habitIds = habits.Select(h => h.Id).ToHashSet();
        var habitLogs = await repos.HabitLogs.FindAsync(l => habitIds.Contains(l.HabitId), cancellationToken);
        var logsByHabit = habitLogs
            .GroupBy(l => l.HabitId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Date).ToList());

        var goals = await repos.Goals.FindAsync(g => g.UserId == request.UserId, cancellationToken);
        var goalIds = goals.Select(g => g.Id).ToHashSet();
        var progressLogs = await repos.GoalProgressLogs.FindAsync(p => goalIds.Contains(p.GoalId), cancellationToken);
        var progressByGoal = progressLogs
            .GroupBy(p => p.GoalId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.CreatedAtUtc).ToList());

        var tags = await repos.Tags.FindAsync(t => t.UserId == request.UserId, cancellationToken);
        var notifications = await repos.Notifications.FindAsync(n => n.UserId == request.UserId, cancellationToken);
        var checklistTemplates = await repos.ChecklistTemplates.FindAsync(c => c.UserId == request.UserId, cancellationToken);
        var achievements = await repos.UserAchievements.FindAsync(a => a.UserId == request.UserId, cancellationToken);
        var streakFreezes = await repos.StreakFreezes.FindAsync(s => s.UserId == request.UserId, cancellationToken);
        var referrals = await repos.Referrals.FindAsync(r => r.ReferrerId == request.UserId, cancellationToken);
        var apiKeys = await repos.ApiKeys.FindAsync(k => k.UserId == request.UserId, cancellationToken);
        var proactiveItems = await repos.ProactiveItems.FindAsync(i => i.UserId == request.UserId, cancellationToken);
        var notionConnections = await repos.NotionConnections.FindAsync(c => c.UserId == request.UserId, cancellationToken);
        var notionTasks = await repos.NotionTasks.FindAsync(t => t.UserId == request.UserId, cancellationToken);
        var notion = notionConnections.SingleOrDefault();

        var exportedAtUtc = DateTime.UtcNow;
        var retentionCutoffAtUtc = exportedAtUtc.AddDays(-ConversationTurn.RetentionDays);
        var conversations = await repos.ConversationTurns.FindAsync(
            turn => turn.UserId == request.UserId && turn.CreatedAtUtc >= retentionCutoffAtUtc, cancellationToken);
        var export = new UserDataExport(
            exportedAtUtc,
            new ExportedAccount(user.Name, user.Email, user.CreatedAtUtc, user.HasProAccess ? "pro" : "free"),
            new ExportedSettings(
                user.TimeZone,
                user.Language,
                user.WeekStartDay,
                user.ThemePreference,
                user.ColorScheme,
                user.AiSummaryEnabled,
                user.EffectiveProactivePushEnabled,
                user.Uses24HourClockPreference,
                user.ProactiveAstraEnabled),
            new ExportedSubscription(
                user.Plan.ToString(),
                user.IsLifetimePro,
                user.SubscriptionSource?.ToString(),
                user.SubscriptionInterval?.ToString(),
                user.PlanExpiresAt,
                user.TrialEndsAt),
            habits.Select(h => MapHabit(h, logsByHabit)).ToList(),
            goals.Select(g => MapGoal(g, progressByGoal, freshProgressValues)).ToList(),
            tags.Select(t => new ExportedTag(t.Id, t.Name, t.Color, t.CreatedAtUtc)).ToList(),
            notifications
                .OrderBy(n => n.CreatedAtUtc)
                .Select(n => new ExportedNotification(n.Id, n.Title, n.Body, n.Url, n.IsRead, n.CreatedAtUtc))
                .ToList(),
            checklistTemplates
                .OrderBy(c => c.CreatedAtUtc)
                .Select(c => new ExportedChecklistTemplate(c.Id, c.Name, c.Items, c.CreatedAtUtc))
                .ToList(),
            achievements
                .OrderBy(a => a.EarnedAtUtc)
                .Select(a => new ExportedAchievement(a.AchievementId, a.EarnedAtUtc))
                .ToList(),
            streakFreezes
                .OrderBy(s => s.UsedOnDate)
                .Select(s => new ExportedStreakFreeze(s.UsedOnDate, s.CreatedAtUtc))
                .ToList(),
            referrals
                .OrderBy(r => r.CreatedAtUtc)
                .Select(r => new ExportedReferral(r.Status.ToString(), r.CreatedAtUtc, r.CompletedAtUtc, r.RewardGrantedAtUtc))
                .ToList(),
            apiKeys
                .OrderBy(k => k.CreatedAtUtc)
                .Select(MapApiKey)
                .ToList(),
            [], [], [], [], [],
            proactiveItems.OrderBy(i => i.CreatedAtUtc).Select(i => new ExportedProactiveItem(
                i.Id, i.Trigger.ToString(), i.LocalDate, i.Text, i.ActionLabel, i.ActionPrompt,
                i.CreatedAtUtc, i.ShownAtUtc, i.PushedAtUtc, i.OpenedAtUtc, i.ActedAtUtc,
                i.EndedAtUtc, i.EndReason?.ToString())).ToList(),
            notion is null ? null : new ExportedNotionConnection(notion.WorkspaceName, notion.Status.ToString(), notion.CreatedAtUtc, notion.LastPollAtUtc),
            notionTasks.Select(NotionTaskResponse.FromEntity).ToArray(),
            conversations.OrderBy(turn => turn.CreatedAtUtc).ThenBy(turn => turn.Role == "user" ? 0 : 1)
                .Select(turn => new ExportedConversationTurn(turn.TurnId, turn.Role, turn.CreatedAtUtc,
                    turn.Model, turn.Content, System.Text.Json.JsonSerializer.Deserialize<
                        Orbit.Application.Chat.ConversationToolCall[]>(turn.ToolCallsJson) ?? [], turn.ResponseJson)).ToArray());

        return Result.Success(export);
    }

    private static ExportedApiKey MapApiKey(ApiKey apiKey)
    {
        return new ExportedApiKey(
            apiKey.Id,
            apiKey.Name,
            apiKey.KeyPrefix,
            apiKey.Scopes,
            apiKey.IsReadOnly,
            apiKey.CreatedAtUtc,
            apiKey.ExpiresAtUtc,
            apiKey.LastUsedAtUtc,
            apiKey.IsRevoked);
    }

    private static ExportedHabit MapHabit(Habit habit, Dictionary<Guid, List<HabitLog>> logsByHabit)
    {
        var logs = logsByHabit.TryGetValue(habit.Id, out var habitLogs)
            ? habitLogs.Select(l => new ExportedHabitLog(l.Date, l.Value, l.Note, l.CreatedAtUtc)).ToList()
            : [];

        return new ExportedHabit(
            habit.Id,
            habit.Title,
            habit.Description,
            habit.Emoji,
            habit.IsBadHabit,
            habit.Schedule == HabitSchedule.General,
            habit.DueDate,
            habit.EndDate,
            habit.FrequencyUnit?.ToString(),
            habit.FrequencyQuantity,
            habit.Days.Select(d => d.ToString()).ToList(),
            habit.ChecklistItems.ToList(),
            habit.CreatedAtUtc,
            logs,
            habit.IntervalWeeks);
    }

    private static ExportedGoal MapGoal(
        Goal goal,
        Dictionary<Guid, List<GoalProgressLog>> progressByGoal,
        IReadOnlyDictionary<Guid, int> freshProgressValues)
    {
        var progress = progressByGoal.TryGetValue(goal.Id, out var goalLogs)
            ? goalLogs.Select(p => new ExportedGoalProgressLog(p.Value, p.PreviousValue, p.Note, p.CreatedAtUtc)).ToList()
            : [];

        var currentValue = freshProgressValues.TryGetValue(goal.Id, out var fresh) ? fresh : goal.CurrentValue;

        return new ExportedGoal(
            goal.Id,
            goal.Title,
            goal.Description,
            goal.TargetValue,
            currentValue,
            goal.Unit,
            goal.Status.ToString(),
            goal.Type.ToString(),
            goal.Deadline,
            goal.CreatedAtUtc,
            goal.CompletedAtUtc,
            progress);
    }
}
