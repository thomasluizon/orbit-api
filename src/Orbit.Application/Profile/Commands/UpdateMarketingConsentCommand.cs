using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Profile.Commands;

public record UpdateMarketingConsentCommand(Guid UserId, bool Enabled) : IRequest<Result>;

public partial class UpdateMarketingConsentCommandHandler(
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork,
    IMarketingContactsService contactsService,
    ILogger<UpdateMarketingConsentCommandHandler> logger) : IRequestHandler<UpdateMarketingConsentCommand, Result>
{
    public async Task<Result> Handle(UpdateMarketingConsentCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedAsync(
            u => u.Id == request.UserId,
            cancellationToken: cancellationToken);

        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        user.SetMarketingConsent(request.Enabled);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await SyncResendContactAsync(user, request.Enabled, cancellationToken);

        return Result.Success();
    }

    private async Task SyncResendContactAsync(User user, bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            if (enabled)
                await contactsService.UpsertProductContactAsync(
                    user.Email, user.Language, user.HasProAccess ? "pro" : "free", cancellationToken);
            else
                await contactsService.SetContactUnsubscribedAsync(user.Email, unsubscribed: true, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogResendSyncFailed(logger, ex, user.Id);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Marketing consent persisted but Resend sync failed for user {UserId}")]
    private static partial void LogResendSyncFailed(ILogger logger, Exception ex, Guid userId);
}
