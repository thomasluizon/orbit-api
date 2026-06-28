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
    IGenericRepository<UserAchievement> Achievements);

public record SendCheerCommand(Guid UserId, Guid RecipientId, Guid HabitId, string? Note) : IRequest<Result<Guid>>;

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

        var habitBelongsToRecipient = await repositories.Habits.AnyAsync(
            h => h.Id == request.HabitId && h.UserId == request.RecipientId,
            cancellationToken);
        if (!habitBelongsToRecipient)
            return Result.Failure<Guid>(ErrorMessages.HabitNotFound);

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var moderation = await ModerateNoteAsync(note, request.UserId, cancellationToken);
        if (moderation.IsFailure)
            return moderation.PropagateError<Guid>();

        var createResult = Cheer.Create(request.UserId, request.RecipientId, request.HabitId, note);
        if (createResult.IsFailure)
            return createResult.PropagateError<Guid>();

        await repositories.Cheers.AddAsync(createResult.Value, cancellationToken);
        await AwardFirstCheerAsync(sender, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await NotifyRecipientAsync(recipient, sender, cancellationToken);

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

    private async Task AwardFirstCheerAsync(User sender, CancellationToken cancellationToken)
    {
        var alreadyEarned = await repositories.Achievements.AnyAsync(
            a => a.UserId == sender.Id && a.AchievementId == AchievementDefinitions.FirstCheer,
            cancellationToken);
        if (alreadyEarned)
            return;

        var earned = new HashSet<string>();
        var newAchievements = new List<(UserAchievement Entity, AchievementDefinition Definition)>();
        AchievementChecks.TryGrant(AchievementDefinitions.FirstCheer, sender, earned, newAchievements);

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

    private async Task NotifyRecipientAsync(User recipient, User sender, CancellationToken cancellationToken)
    {
        var isPortuguese = LocaleHelper.IsPortuguese(recipient.Language);
        var title = isPortuguese ? "Novo incentivo" : "New cheer";
        var body = isPortuguese
            ? $"{sender.Name} torceu por você!"
            : $"{sender.Name} cheered you on!";

        try
        {
            await pushNotificationService.SendToUserAsync(recipient.Id, title, body, cancellationToken: cancellationToken);
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
