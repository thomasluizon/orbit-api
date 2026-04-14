using MediatR;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.ApiKeys.Queries;

public record ApiKeyResponse(
    Guid Id,
    string Name,
    string KeyPrefix,
    IReadOnlyList<string> Scopes,
    bool IsReadOnly,
    DateTime? ExpiresAtUtc,
    DateTime CreatedAtUtc,
    DateTime? LastUsedAtUtc,
    bool IsRevoked);

public record GetApiKeysQuery(Guid UserId) : IRequest<Result<IReadOnlyList<ApiKeyResponse>>>;

public class GetApiKeysQueryHandler(
    IGenericRepository<ApiKey> apiKeyRepository) : IRequestHandler<GetApiKeysQuery, Result<IReadOnlyList<ApiKeyResponse>>>
{
    public async Task<Result<IReadOnlyList<ApiKeyResponse>>> Handle(GetApiKeysQuery request, CancellationToken cancellationToken)
    {
        var keys = await apiKeyRepository.FindAsync(
            k => k.UserId == request.UserId,
            cancellationToken);

        var result = keys
            .OrderByDescending(k => k.CreatedAtUtc)
            .Select(k => new ApiKeyResponse(
                k.Id,
                k.Name,
                k.KeyPrefix,
                k.Scopes,
                k.IsReadOnly,
                k.ExpiresAtUtc,
                k.CreatedAtUtc,
                k.LastUsedAtUtc,
                k.IsRevoked))
            .ToList();

        return Result.Success<IReadOnlyList<ApiKeyResponse>>(result);
    }
}
