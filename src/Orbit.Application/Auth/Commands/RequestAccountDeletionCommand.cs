using System.Security.Cryptography;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Auth.Jobs;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record RequestAccountDeletionCommand(Guid UserId) : IRequest<Result>;

public class RequestAccountDeletionCommandHandler(
    IMemoryCache cache,
    IGenericRepository<User> userRepository,
    IBackgroundJobClient backgroundJobClient) : IRequestHandler<RequestAccountDeletionCommand, Result>
{
    public async Task<Result> Handle(RequestAccountDeletionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var cacheKey = $"delete:{user.Email.ToLowerInvariant()}";

        if (cache.TryGetValue(cacheKey, out VerificationEntry? existing) && existing is not null)
        {
#pragma warning disable ORBIT0004 // WHY: pre-existing deliberate UTC instant (expiry/TTL/cutoff math, not a user-facing date), per-site justification ledger: https://github.com/thomasluizon/orbit-api/issues/431
            var elapsed = DateTime.UtcNow - existing.CreatedAt;
#pragma warning restore ORBIT0004
            if (elapsed.TotalSeconds < 60)
                return Result.Failure(ErrorMessages.CodeRequestCooldown);
        }

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

#pragma warning disable ORBIT0004 // WHY: pre-existing deliberate UTC instant (expiry/TTL/cutoff math, not a user-facing date), per-site justification ledger: https://github.com/thomasluizon/orbit-api/issues/431
        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
#pragma warning restore ORBIT0004
        cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
        });

        backgroundJobClient.Enqueue<SendAccountDeletionCodeEmailJob>(
            job => job.ExecuteAsync(user.Email, code, user.Language ?? "en"));

        return Result.Success();
    }
}
