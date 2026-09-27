using MediatR;
using Microsoft.Extensions.Logging;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Marketing.Commands;

public record UnsubscribeMarketingCommand(string Token) : IRequest<Result>;

public partial class UnsubscribeMarketingCommandHandler(
    IMarketingUnsubscribeTokenService unsubscribeTokenService,
    IGenericRepository<User> userRepository,
    IGenericRepository<MarketingContact> contactRepository,
    IUnitOfWork unitOfWork,
    ILogger<UnsubscribeMarketingCommandHandler> logger) : IRequestHandler<UnsubscribeMarketingCommand, Result>
{
    public async Task<Result> Handle(UnsubscribeMarketingCommand request, CancellationToken cancellationToken)
    {
        if (!unsubscribeTokenService.TryValidateToken(request.Token, out var userId))
            return Result.Failure(ErrorMessages.InvalidUnsubscribeToken);

        var user = await userRepository.FindOneTrackedAsync(
            candidate => candidate.Id == userId,
            cancellationToken: cancellationToken);

        if (user is null)
        {
            var contact = await contactRepository.FindOneTrackedAsync(
                candidate => candidate.Id == userId,
                cancellationToken: cancellationToken);
            if (contact is null || contact.UnsubscribedAtUtc is not null)
                return Result.Success();

            contact.Unsubscribe();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }

        if (user.MarketingEmailConsent == false)
            return Result.Success();

        user.SetMarketingConsent(false);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        LogConsentRevoked(logger, user.Id);
        return Result.Success();
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Marketing consent revoked for user {UserId} via unsubscribe link")]
    private static partial void LogConsentRevoked(ILogger logger, Guid userId);
}
