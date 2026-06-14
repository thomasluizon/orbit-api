using MediatR;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Profile.Models;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Queries;

public record ExportUserDataQuery(Guid UserId) : IRequest<Result<UserDataExport>>;

public class ExportUserDataQueryHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<Habit> habitRepository,
    IGenericRepository<HabitLog> habitLogRepository,
    IGenericRepository<Goal> goalRepository,
    IGenericRepository<GoalProgressLog> goalProgressLogRepository,
    IGenericRepository<Tag> tagRepository,
    IGenericRepository<UserFact> userFactRepository,
    IGenericRepository<Notification> notificationRepository,
    IGenericRepository<ChecklistTemplate> checklistTemplateRepository,
    IGenericRepository<UserAchievement> userAchievementRepository,
    IGenericRepository<StreakFreeze> streakFreezeRepository,
    IGenericRepository<Referral> referralRepository,
    IGenericRepository<ApiKey> apiKeyRepository,
    IUserDateService userDateService,
    IStreakGoalReadSyncer streakGoalReadSyncer)
    : IRequestHandler<ExportUserDataQuery, Result<UserDataExport>>
{
    public async Task<Result<UserDataExport>> Handle(ExportUserDataQuery request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);

        if (user is null)
            return Result.Failure<UserDataExport>(ErrorMessages.UserNotFound);

        var userToday = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);
        var freshStreakValues = await streakGoalReadSyncer.ComputeFreshValuesAsync(request.UserId, userToday, cancellationToken);

        var habits = await habitRepository.FindAsync(h => h.UserId == request.UserId, cancellationToken);
        var habitIds = habits.Select(h => h.Id).ToHashSet();
        var habitLogs = await habitLogRepository.FindAsync(l => habitIds.Contains(l.HabitId), cancellationToken);
        var logsByHabit = habitLogs
            .GroupBy(l => l.HabitId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.Date).ToList());

        var goals = await goalRepository.FindAsync(g => g.UserId == request.UserId, cancellationToken);
        var goalIds = goals.Select(g => g.Id).ToHashSet();
        var progressLogs = await goalProgressLogRepository.FindAsync(p => goalIds.Contains(p.GoalId), cancellationToken);
        var progressByGoal = progressLogs
            .GroupBy(p => p.GoalId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.CreatedAtUtc).ToList());

        var tags = await tagRepository.FindAsync(t => t.UserId == request.UserId, cancellationToken);
        var facts = await userFactRepository.FindAsync(f => f.UserId == request.UserId, cancellationToken);
        var notifications = await notificationRepository.FindAsync(n => n.UserId == request.UserId, cancellationToken);
        var checklistTemplates = await checklistTemplateRepository.FindAsync(c => c.UserId == request.UserId, cancellationToken);
        var achievements = await userAchievementRepository.FindAsync(a => a.UserId == request.UserId, cancellationToken);
        var streakFreezes = await streakFreezeRepository.FindAsync(s => s.UserId == request.UserId, cancellationToken);
        var referrals = await referralRepository.FindAsync(r => r.ReferrerId == request.UserId, cancellationToken);
        var apiKeys = await apiKeyRepository.FindAsync(k => k.UserId == request.UserId, cancellationToken);

        var exportedAtUtc = DateTime.UtcNow;
        var export = new UserDataExport(
            exportedAtUtc,
            new ExportedAccount(user.Name, user.Email, user.CreatedAtUtc, user.HasProAccess ? "pro" : "free"),
            new ExportedSettings(
                user.TimeZone,
                user.Language,
                user.WeekStartDay,
                user.ThemePreference,
                user.ColorScheme,
                user.AiMemoryEnabled,
                user.AiSummaryEnabled),
            new ExportedSubscription(
                user.Plan.ToString(),
                user.IsLifetimePro,
                user.SubscriptionSource?.ToString(),
                user.SubscriptionInterval?.ToString(),
                user.PlanExpiresAt,
                user.TrialEndsAt),
            habits.Select(h => MapHabit(h, logsByHabit)).ToList(),
            goals.Select(g => MapGoal(g, progressByGoal, freshStreakValues)).ToList(),
            tags.Select(t => new ExportedTag(t.Id, t.Name, t.Color, t.CreatedAtUtc)).ToList(),
            facts.Select(f => new ExportedUserFact(f.FactText, f.Category, f.ExtractedAtUtc)).ToList(),
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
                .ToList());

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

    private static ExportedHabit MapHabit(Habit habit, IReadOnlyDictionary<Guid, List<HabitLog>> logsByHabit)
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
            habit.IsGeneral,
            habit.DueDate,
            habit.EndDate,
            habit.FrequencyUnit?.ToString(),
            habit.FrequencyQuantity,
            habit.Days.Select(d => d.ToString()).ToList(),
            habit.ChecklistItems.ToList(),
            habit.CreatedAtUtc,
            logs);
    }

    private static ExportedGoal MapGoal(
        Goal goal,
        IReadOnlyDictionary<Guid, List<GoalProgressLog>> progressByGoal,
        IReadOnlyDictionary<Guid, int> freshStreakValues)
    {
        var progress = progressByGoal.TryGetValue(goal.Id, out var goalLogs)
            ? goalLogs.Select(p => new ExportedGoalProgressLog(p.Value, p.PreviousValue, p.Note, p.CreatedAtUtc)).ToList()
            : [];

        var currentValue = freshStreakValues.TryGetValue(goal.Id, out var fresh) ? fresh : goal.CurrentValue;

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
