using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record ConfirmAccountDeletionCommand(Guid UserId, string Code) : IRequest<Result>;

public class ConfirmAccountDeletionCommandHandler(
    IMemoryCache cache,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<ConfirmAccountDeletionCommand, Result>
{
    public async Task<Result> Handle(ConfirmAccountDeletionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure("User not found");

        var email = user.Email.ToLowerInvariant();
        var cacheKey = $"delete:{email}";

        if (!cache.TryGetValue(cacheKey, out VerificationEntry? entry) || entry is null)
            return Result.Failure("Deletion code expired or not found");

        if (entry.Attempts >= 3)
        {
            cache.Remove(cacheKey);
            return Result.Failure("Too many attempts. Please request a new code");
        }

        if (entry.Code != request.Code)
        {
            var updated = new VerificationEntry(entry.Code, entry.Attempts + 1, entry.CreatedAt);
            var remaining = TimeSpan.FromMinutes(10) - (DateTime.UtcNow - entry.CreatedAt);
            if (remaining > TimeSpan.Zero)
            {
                cache.Set(cacheKey, updated, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = remaining
                });
            }
            return Result.Failure("Invalid code");
        }

        cache.Remove(cacheKey);

        // Delete user - EF Core cascades all related data
        userRepository.Remove(user);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
