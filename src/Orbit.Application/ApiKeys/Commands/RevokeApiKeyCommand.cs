using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.ApiKeys.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ApiKeys.Commands;

public record RevokeApiKeyCommand(
    Guid UserId,
    Guid KeyId) : IRequest<Result>;

public class RevokeApiKeyCommandHandler(
    IGenericRepository<ApiKey> apiKeyRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    IMemoryCache cache,
    ApiKeyManagementAuthorization authorization) : IRequestHandler<RevokeApiKeyCommand, Result>
{
    public async Task<Result> Handle(RevokeApiKeyCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanManageApiKeys(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck;

        var requiresStepUp = await authorization.IsRequiredAsync(cancellationToken);
        if (requiresStepUp && !authorization.HasGrant(request.UserId))
            return Result.Failure(ErrorMessages.ApiKeyCreationChallengeRequired);

        var apiKey = await FindKeyAsync(request, cancellationToken);
        if (apiKey is null)
            return Result.Failure(ErrorMessages.ApiKeyNotFound);

        for (var attempt = 0; attempt < 2 && !apiKey.IsRevoked; attempt++)
        {
            apiKey.Revoke();
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException)
            {
                unitOfWork.ResetTracking();
                apiKey = await FindKeyAsync(request, cancellationToken);
                if (apiKey is null)
                    return Result.Failure(ErrorMessages.ApiKeyNotFound);
                if (apiKey.IsRevoked)
                    break;
                if (attempt > 0)
                    throw;
            }
        }

        cache.Remove(ReferenceCacheKeys.ApiKeys(request.UserId));

        return Result.Success();
    }

    private async Task<ApiKey?> FindKeyAsync(RevokeApiKeyCommand request, CancellationToken cancellationToken)
    {
        var keys = await apiKeyRepository.FindTrackedAsync(
            key => key.Id == request.KeyId && key.UserId == request.UserId, cancellationToken);
        return keys.Count > 0 ? keys[0] : null;
    }
}
