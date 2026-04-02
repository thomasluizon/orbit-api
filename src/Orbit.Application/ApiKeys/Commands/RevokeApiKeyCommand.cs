using MediatR;
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
    IUnitOfWork unitOfWork) : IRequestHandler<RevokeApiKeyCommand, Result>
{
    public async Task<Result> Handle(RevokeApiKeyCommand request, CancellationToken cancellationToken)
    {
        var keys = await apiKeyRepository.FindTrackedAsync(
            k => k.Id == request.KeyId && k.UserId == request.UserId,
            cancellationToken);

        var apiKey = keys.FirstOrDefault();
        if (apiKey is null)
            return Result.Failure(ErrorMessages.ApiKeyNotFound, ErrorCodes.ApiKeyNotFound);

        apiKey.Revoke();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
