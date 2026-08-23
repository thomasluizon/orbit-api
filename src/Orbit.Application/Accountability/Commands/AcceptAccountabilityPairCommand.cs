using MediatR;
using Orbit.Application.Accountability.Services;
using Orbit.Application.Common;
using Orbit.Application.Social.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Accountability.Commands;

public record AcceptAccountabilityPairCommand(
    Guid UserId,
    Guid PairId,
    IReadOnlyList<Guid> HabitIds) : IRequest<Result>;

public class AcceptAccountabilityPairCommandHandler(
    SocialAccessGuard socialAccessGuard,
    AccountabilityPairService accountabilityPairService,
    AccountabilityRepositories repositories,
    SocialNotificationDispatcher notificationDispatcher,
    IUnitOfWork unitOfWork) : IRequestHandler<AcceptAccountabilityPairCommand, Result>
{
    public async Task<Result> Handle(AcceptAccountabilityPairCommand request, CancellationToken cancellationToken)
    {
        var access = await socialAccessGuard.EnsureEnabledAsync(request.UserId, cancellationToken);
        if (access.IsFailure)
            return access.PropagateError();
        var accepter = access.Value;

        var pair = await repositories.Pairs.FindOneTrackedAsync(
            p => p.Id == request.PairId && p.AddresseeId == request.UserId,
            cancellationToken: cancellationToken);
        if (pair is null)
            return Result.Failure(ErrorMessages.PairNotFound);

        var acceptResult = pair.Accept();
        if (acceptResult.IsFailure)
            return acceptResult;

        var linkResult = await accountabilityPairService.ReplaceLinkedHabitsAsync(
            pair, request.UserId, request.HabitIds, cancellationToken);
        if (linkResult.IsFailure)
            return linkResult;

        var requester = await repositories.Users.FindOneTrackedAsync(
            u => u.Id == pair.RequesterId,
            cancellationToken: cancellationToken);

        var notification = BuildRequesterNotification(requester, accepter);
        if (notification is not null)
            await notificationDispatcher.StageAsync(notification, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (notification is not null)
            await notificationDispatcher.PushAsync(notification, cancellationToken);

        return Result.Success();
    }

    private static Notification? BuildRequesterNotification(User? requester, User accepter)
    {
        if (requester is null || !requester.SocialOptIn)
            return null;

        var isPortuguese = LocaleHelper.IsPortuguese(requester.Language);
        var title = isPortuguese ? "Parceria aceita" : "Accountability invite accepted";
        var body = isPortuguese
            ? $"{accepter.Name} aceitou seu convite de parceria."
            : $"{accepter.Name} accepted your accountability invite.";

        return Notification.Create(requester.Id, title, body, null);
    }
}
