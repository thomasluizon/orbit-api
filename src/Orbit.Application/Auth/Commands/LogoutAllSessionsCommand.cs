using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record LogoutAllSessionsCommand(Guid UserId) : IRequest<Result>;

public class LogoutAllSessionsCommandHandler(IAuthSessionService authSessionService,
    IGenericRepository<ApiKey> apiKeyRepository, IUnitOfWork unitOfWork)
    : IRequestHandler<LogoutAllSessionsCommand, Result>
{
    public Task<Result> Handle(LogoutAllSessionsCommand request, CancellationToken cancellationToken)
        => unitOfWork.ExecuteInTransactionAsync(async token =>
    {
        var result = await authSessionService.RevokeAllSessionsAsync(request.UserId, token);
        if (result.IsFailure)
            return result;
        foreach (var key in await apiKeyRepository.FindTrackedAsync(
            key => key.UserId == request.UserId && key.OAuthFamilyId != null && !key.IsRevoked, token))
            key.Revoke();
        await unitOfWork.SaveChangesAsync(token);
        return Result.Success();
    }, cancellationToken);
}
