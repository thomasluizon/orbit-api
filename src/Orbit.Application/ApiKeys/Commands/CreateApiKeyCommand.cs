using MediatR;
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
    DateTime CreatedAtUtc);

public record CreateApiKeyCommand(
    Guid UserId,
    string Name) : IRequest<Result<CreateApiKeyResponse>>;

public class CreateApiKeyCommandHandler(
    IGenericRepository<ApiKey> apiKeyRepository,
    IPayGateService payGate,
    IUnitOfWork unitOfWork) : IRequestHandler<CreateApiKeyCommand, Result<CreateApiKeyResponse>>
{
    private const int MaxActiveKeys = 5;

    public async Task<Result<CreateApiKeyResponse>> Handle(CreateApiKeyCommand request, CancellationToken cancellationToken)
    {
        var gateCheck = await payGate.CanCreateApiKeys(request.UserId, cancellationToken);
        if (gateCheck.IsFailure)
            return gateCheck.PropagateError<CreateApiKeyResponse>();

        var activeKeys = await apiKeyRepository.FindAsync(
            k => k.UserId == request.UserId && !k.IsRevoked,
            cancellationToken);

        if (activeKeys.Count >= MaxActiveKeys)
            return Result.Failure<CreateApiKeyResponse>($"You can have at most {MaxActiveKeys} active API keys.");

        var createResult = ApiKey.Create(request.UserId, request.Name);
        if (createResult.IsFailure)
            return Result.Failure<CreateApiKeyResponse>(createResult.Error);

        var (apiKey, rawKey) = createResult.Value;

        await apiKeyRepository.AddAsync(apiKey, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(new CreateApiKeyResponse(
            apiKey.Id,
            apiKey.Name,
            rawKey,
            apiKey.KeyPrefix,
            apiKey.CreatedAtUtc));
    }
}
