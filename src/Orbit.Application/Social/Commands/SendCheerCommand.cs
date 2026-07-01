using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Application.Gamification;
using Orbit.Application.Gamification.Models;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Social.Commands;

/// <summary>Groups the repositories a cheer touches to keep the handler constructor small.</summary>
public record SendCheerRepositories(
    IGenericRepository<User> Users,
    IGenericRepository<Habit> Habits,
    IGenericRepository<Cheer> Cheers,
    IGenericRepository<UserAchievement> Achievements,
    IGenericRepository<Notification> Notifications);

public record SendCheerCommand(Guid UserId, Guid RecipientId, Guid? HabitId, string? Note) : IRequest<Result<Guid>>;

public partial class SendCheerCommandHandler(
    SocialAccessGuard socialAccessGuard,
    FriendGraphService friendGraphService,
    SendCheerRepositories repositories,
    IContentModerationService contentModerationService,
    IPushNotificationService pushNotificationService,
    IXpAwarder xpAwarder,
    IUnitOfWork unitOfWork,
    ILogger<SendCheerCommandHandler> logger) : IRequestHandler<SendCheerCommand, Result<Guid>>
{
    private const string CheerleaderAchievementId = "cheerleader";
    private const int CheerleaderThreshold = 10;
    private const string RecipientNotificationUrl = "/social?tab=feed";

    public async Task<Result<Guid>> Handle(SendCheerCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<Guid>();
        var sender = access.Value;

        var recipient = await repositories.Users.FindOneTrackedAsync(
            u => u.Id == request.RecipientId,
            cancellationToken: cancellationToken);
        if (recipient is null || !recipient.SocialOptIn)
            return Result.Failure<Guid>(ErrorMessages.NotFriends);

        if (!await friendGraphService.AreAcceptedFriendsAsync(request.UserId, request.RecipientId, cancellationToken))
            return Result.Failure<Guid>(ErrorMessages.NotFriends);

        if (await friendGraphService.IsBlockedBetweenAsync(request.UserId, request.RecipientId, cancellationToken))
            return Result.Failure<Guid>(ErrorMessages.Blocked);

        if (request.HabitId.HasValue)
        {
            var habitBelongsToRecipient = await repositories.Habits.AnyAsync(
                h => h.Id == request.HabitId.Value && h.UserId == request.RecipientId,
                cancellationToken);
            if (!habitBelongsToRecipient)
                return Result.Failure<Guid>(ErrorMessages.HabitNotFound);
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var moderation = await ModerateNoteAsync(note, request.UserId, cancellationToken);
        if (moderation.IsFailure)
            return moderation.PropagateError<Guid>();

        var createResult = Cheer.Create(request.UserId, request.RecipientId, request.HabitId, note);
        if (createResult.IsFailure)
            return createResult.PropagateError<Guid>();

        await repositories.Cheers.AddAsync(createResult.Value, cancellationToken);
        await AwardFirstCheerAsync(sender, cancellationToken);
        await AwardCheerleaderAsync(sender, cancellationToken);

        var notification = BuildRecipientNotification(recipient, sender);
        await repositories.Notifications.AddAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await PushRecipientAsync(recipient, notification, cancellationToken);

        return Result.Success(createResult.Value.Id);
    }

    /// <summary>
    /// Screens the note before persistence. A definitive flag rejects the cheer (fail closed); a
    /// moderation outage proceeds and is logged at warning (fail open), since cheer notes are visible
    /// only to an accepted friend and are already covered by block + report. An empty note skips the call.
    /// </summary>
    private async Task<Result> ModerateNoteAsync(string? note, Guid senderId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(note))
            return Result.Success();

        var moderation = await contentModerationService.CheckTextAsync(note, cancellationToken);

        if (moderation.Flagged && !moderation.Unavailable)
            return Result.Failure(ErrorMessages.ContentRejected);

        if (moderation.Unavailable)
            LogModerationUnavailable(logger, senderId);

        return Result.Success();
    }

    private Task AwardFirstCheerAsync(User sender, CancellationToken cancellationToken) =>
        TryGrantAchievementAsync(sender, AchievementDefinitions.FirstCheer, cancellationToken);

    /// <summary>
    /// Awards "Cheerleader" once the sender has sent <see cref="CheerleaderThreshold"/> cheers (counting
    /// the one being persisted in this unit of work, which is not yet in the database). The conventional
    /// achievement id stays dormant — <see cref="AchievementChecks.TryGrant"/> no-ops on an unknown id —
    /// until the definition ships, then this lights up automatically.
    /// See https://github.com/thomasluizon/orbit-ui-mobile/issues/196.
    /// </summary>
    private async Task AwardCheerleaderAsync(User sender, CancellationToken cancellationToken)
    {
        var priorCheerCount = await repositories.Cheers.CountAsync(
            c => c.SenderId == sender.Id, cancellationToken);
        if (priorCheerCount + 1 < CheerleaderThreshold)
            return;

        await TryGrantAchievementAsync(sender, CheerleaderAchievementId, cancellationToken);
    }

    private async Task TryGrantAchievementAsync(User sender, string achievementId, CancellationToken cancellationToken)
    {
        var alreadyEarned = await repositories.Achievements.AnyAsync(
            a => a.UserId == sender.Id && a.AchievementId == achievementId,
            cancellationToken);
        if (alreadyEarned)
            return;

        var earned = new HashSet<string>();
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        AchievementChecks.TryGrant(achievementId, sender, earned, newAchievements);

        if (newAchievements.Count == 0)
            return;

        await repositories.Achievements.AddAsync(newAchievements[0].Entity, cancellationToken);
        await xpAwarder.AwardAsync(
            sender, newAchievements[0].Definition.XpReward, XpAwardSource.Achievement,
            newAchievements[0].Entity.Id, awardedAtUtc: DateTime.UtcNow, cancellationToken);

        var newLevel = LevelDefinitions.GetLevelForXp(sender.TotalXp);
        if (newLevel.Level != sender.Level)
            sender.SetLevel(newLevel.Level);
    }

    private static Notification BuildRecipientNotification(User recipient, User sender)
    {
        var isPortuguese = LocaleHelper.IsPortuguese(recipient.Language);
        var title = isPortuguese ? "Novo incentivo" : "New cheer";
        var body = isPortuguese
            ? $"{sender.Name} torceu por você!"
            : $"{sender.Name} cheered you on!";

        return Notification.Create(recipient.Id, title, body, RecipientNotificationUrl);
    }

    private async Task PushRecipientAsync(User recipient, Notification notification, CancellationToken cancellationToken)
    {
        try
        {
            await pushNotificationService.SendToUserAsync(
                recipient.Id, notification.Title, notification.Body, notification.Url, cancellationToken);
        }
        catch (Exception ex)
        {
            LogPushNotificationFailed(logger, ex, recipient.Id);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Cheer note moderation unavailable for sender {UserId}; allowing note (fail open)")]
    private static partial void LogModerationUnavailable(ILogger logger, Guid userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Cheer push failed for user {UserId}")]
    private static partial void LogPushNotificationFailed(ILogger logger, Exception ex, Guid userId);
}
