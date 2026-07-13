using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Commands;

public record CheckInAccountabilityCommand(Guid UserId, Guid PairId, string? Note) : IRequest<Result<Guid>>;

public partial class CheckInAccountabilityCommandHandler(
    SocialInteractionServices social,
    AccountabilityPairService accountabilityPairService,
    AccountabilityRepositories repositories,
    IContentModerationService contentModerationService,
    IUserDateService userDateService,
    IUnitOfWork unitOfWork,
    ILogger<CheckInAccountabilityCommandHandler> logger) : IRequestHandler<CheckInAccountabilityCommand, Result<Guid>>
{
    private const string BuddyNotificationUrl = "/social?tab=buddies";

    public async Task<Result<Guid>> Handle(CheckInAccountabilityCommand request, CancellationToken cancellationToken)
    {
        var access = await social.AccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError<Guid>();
        var checker = access.Value;

        var pair = await accountabilityPairService.FindParticipantPairAsync(request.PairId, request.UserId, cancellationToken);
        if (pair is null || pair.Status != AccountabilityPairStatus.Accepted)
            return Result.Failure<Guid>(ErrorMessages.PairNotFound);

        var buddyId = pair.RequesterId == request.UserId ? pair.AddresseeId : pair.RequesterId;

        if (await social.FriendGraph.IsBlockedBetweenAsync(request.UserId, buddyId, cancellationToken))
            return Result.Failure<Guid>(ErrorMessages.Blocked);

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        var moderation = await ModerateNoteAsync(note, request.UserId, cancellationToken);
        if (moderation.IsFailure)
            return moderation.PropagateError<Guid>();

        var today = await userDateService.GetUserTodayAsync(request.UserId, cancellationToken);

        var alreadyCheckedIn = await repositories.CheckIns.AnyAsync(
            c => c.PairId == request.PairId && c.UserId == request.UserId && c.Date == today,
            cancellationToken);
        if (alreadyCheckedIn)
            return Result.Failure<Guid>(ErrorMessages.AlreadyCheckedIn);

        var createResult = AccountabilityCheckIn.Create(request.PairId, request.UserId, today, note);
        if (createResult.IsFailure)
            return createResult.PropagateError<Guid>();

        await repositories.CheckIns.AddAsync(createResult.Value, cancellationToken);

        var buddy = await repositories.Users.FindOneTrackedAsync(
            u => u.Id == buddyId,
            cancellationToken: cancellationToken);
        var notification = BuildBuddyNotification(buddy, checker);
        if (notification is not null)
            await social.NotificationDispatcher.StageAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (notification is not null)
            await social.NotificationDispatcher.PushAsync(notification, cancellationToken);

        return Result.Success(createResult.Value.Id);
    }

    /// <summary>
    /// Screens the note before persistence. A definitive flag rejects the check-in (fail closed); a
    /// moderation outage proceeds and is logged at warning (fail open), since check-in notes are visible
    /// only to an accepted buddy and are already covered by block + report. An empty note skips the call.
    /// </summary>
    private async Task<Result> ModerateNoteAsync(string? note, Guid checkerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(note))
            return Result.Success();

        var moderation = await contentModerationService.CheckTextAsync(note, cancellationToken);

        if (moderation.Flagged && !moderation.Unavailable)
            return Result.Failure(ErrorMessages.ContentRejected);

        if (moderation.Unavailable)
            LogModerationUnavailable(logger, checkerId);

        return Result.Success();
    }

    private static Notification? BuildBuddyNotification(User? buddy, User checker)
    {
        if (buddy is null || !buddy.SocialOptIn)
            return null;

        var isPortuguese = LocaleHelper.IsPortuguese(buddy.Language);
        var title = isPortuguese ? "Novo check-in" : "New check-in";
        var body = isPortuguese
            ? $"{checker.Name} fez check-in com você."
            : $"{checker.Name} checked in with you.";

        return Notification.Create(buddy.Id, title, body, BuddyNotificationUrl);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Check-in note moderation unavailable for user {UserId}; allowing note (fail open)")]
    private static partial void LogModerationUnavailable(ILogger logger, Guid userId);
}
