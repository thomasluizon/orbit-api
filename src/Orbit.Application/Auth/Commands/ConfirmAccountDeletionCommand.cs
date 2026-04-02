using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using System.Security.Cryptography;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

public record ConfirmAccountDeletionCommand(Guid UserId, string Code) : IRequest<Result<DateTime>>;

public class ConfirmAccountDeletionCommandHandler(
    IMemoryCache cache,
    IGenericRepository<User> userRepository,
    IUnitOfWork unitOfWork) : IRequestHandler<ConfirmAccountDeletionCommand, Result<DateTime>>
{
    public async Task<Result<DateTime>> Handle(ConfirmAccountDeletionCommand request, CancellationToken cancellationToken)
    {
        var user = await userRepository.GetByIdAsync(request.UserId, cancellationToken);
        if (user is null)
            return Result.Failure<DateTime>(ErrorMessages.UserNotFound);

        var email = user.Email.ToLowerInvariant();
        var cacheKey = $"delete:{email}";

        if (!cache.TryGetValue(cacheKey, out VerificationEntry? entry) || entry is null)
            return Result.Failure<DateTime>("Deletion code expired or not found");

        if (entry.Attempts >= AppConstants.MaxVerificationAttempts)
        {
            cache.Remove(cacheKey);
            return Result.Failure<DateTime>("Too many attempts. Please request a new code");
        }

        if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(entry.Code),
            System.Text.Encoding.UTF8.GetBytes(request.Code)))
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
            return Result.Failure<DateTime>("Invalid code");
        }

        cache.Remove(cacheKey);

        // Calculate scheduled deletion date
        var scheduledDate = user.HasProAccess && user.PlanExpiresAt.HasValue && user.PlanExpiresAt.Value > DateTime.UtcNow
            ? user.PlanExpiresAt.Value.AddDays(7)
            : DateTime.UtcNow.AddDays(7);

        // Deactivate instead of hard-delete
        user.Deactivate(scheduledDate);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success(scheduledDate);
    }
}
