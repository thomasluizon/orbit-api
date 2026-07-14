using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

/// <summary>
/// Shared post-authentication user provisioning: look up an existing user by email or create one,
/// handling the concurrent-signup race by re-reading the winner on a unique-violation. Callers are
/// responsible for email normalization and any post-creation side effects (welcome email, referrals).
/// </summary>
internal static class AuthUserProvisioning
{
    public static async Task<Result<(User User, bool IsNew)>> FindOrCreateUserAsync(
        IGenericRepository<User> userRepository,
        IUnitOfWork unitOfWork,
        string email,
        string name,
        string language,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.FindOneTrackedIgnoringFiltersAsync(
            u => u.Email == email,
            cancellationToken);

        if (user is not null)
            return Result.Success((user, false));

        var createResult = User.Create(name, email);
        if (createResult.IsFailure)
            return createResult.PropagateError<(User, bool)>();

        user = createResult.Value;
        user.SetLanguage(language);
        user.SeedDefaultHandle();
        await userRepository.AddAsync(user, cancellationToken);

        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (DbUniqueViolation.IsUniqueViolation(exception))
        {
            var raced = await userRepository.FindOneTrackedIgnoringFiltersAsync(
                u => u.Email == email,
                cancellationToken);
            if (raced is null)
                throw;

            return Result.Success((raced, false));
        }

        return Result.Success((user, true));
    }
}
