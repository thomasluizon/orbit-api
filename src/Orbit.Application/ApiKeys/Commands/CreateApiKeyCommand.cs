using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ApiKeys.Commands;

public record CreateApiKeyResponse(
    Guid Id,
    string Name,
    string Key,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    bool IsReadOnly,
    DateTime? ExpiresAtUtc,
    DateTime CreatedAtUtc);

public record CreateApiKeyCommand(
    Guid UserId,
    string Name,
    IReadOnlyList<string>? Scopes = null,
    bool IsReadOnly = false,
    DateTime? ExpiresAtUtc = null) : IRequest<Result<CreateApiKeyResponse>>;

public class CreateApiKeyCommandHandler(
    IGenericRepository<ApiKey> apiKeyRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork,
    IMemoryCache cache) : IRequestHandler<CreateApiKeyCommand, Result<CreateApiKeyResponse>>
{
    private const int MaxActiveKeys = 5;

    public async Task<Result<CreateApiKeyResponse>> Handle(CreateApiKeyCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanCreateApiKeys(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<CreateApiKeyResponse>();

        var activeKeyCount = await apiKeyRepository.CountAsync(
            k => k.UserId == request.UserId && !k.IsRevoked,
            cancellationToken);

        if (activeKeyCount >= MaxActiveKeys)
            return Result.Failure<CreateApiKeyResponse>(ErrorMessages.MaxApiKeys.Format(MaxActiveKeys));

        var createResult = ApiKey.Create(
            request.UserId,
            request.Name,
            request.Scopes,
            request.IsReadOnly,
            request.ExpiresAtUtc);
        if (createResult.IsFailure)
            return createResult.PropagateError<CreateApiKeyResponse>();

        var (apiKey, rawKey) = createResult.Value;

        await apiKeyRepository.AddAsync(apiKey, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        cache.Remove(ReferenceCacheKeys.ApiKeys(request.UserId));

        return Result.Success(new CreateApiKeyResponse(
            apiKey.Id,
            apiKey.Name,
            rawKey,
            apiKey.KeyPrefix,
            apiKey.Scopes,
            apiKey.IsReadOnly,
            apiKey.ExpiresAtUtc,
            apiKey.CreatedAtUtc));
    }
}
