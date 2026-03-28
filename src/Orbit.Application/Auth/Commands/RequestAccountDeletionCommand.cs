using System.Security.Cryptography;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record RequestAccountDeletionCommand(Guid UserId) : IRequest<Result>;

public class RequestAccountDeletionCommandHandler(
    IMemoryCache cache,
    IGenericRepository<User> userRepository,
    IEmailService emailService) : IRequestHandler<RequestAccountDeletionCommand, Result>
{
    public async Task<Result> Handle(RequestAccountDeletionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure(ErrorMessages.UserNotFound);

        var cacheKey = $"delete:{user.Email.ToLowerInvariant()}";

        // Rate limit: no new code within 60 seconds
        if (cache.TryGetValue(cacheKey, out VerificationEntry? existing) && existing is not null)
        {
            var elapsed = DateTime.UtcNow - existing.CreatedAt;
            if (elapsed.TotalSeconds < 60)
                return Result.Failure("Please wait before requesting a new code");
        }

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

        var entry = new VerificationEntry(code, 0, DateTime.UtcNow);
        cache.Set(cacheKey, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
        });

        await emailService.SendAccountDeletionCodeAsync(user.Email, code, user.Language ?? "en", cancellationToken);

        return Result.Success();
    }
}
