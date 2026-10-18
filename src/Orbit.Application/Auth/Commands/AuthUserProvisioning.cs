using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Auth.Commands;

/// <summary>
/// Builds the same candidate and performs the same write and read for every authenticated email.
/// This removes registration-dependent request work; it does not promise exact constant time.
/// </summary>
internal static class AuthUserProvisioning
{
    public static async Task<Result<User>> FindOrCreateUserAsync(
        IAuthUserStore userStore,
        string email,
        string name,
        string language,
        CancellationToken cancellationToken,
        string? referralCode = null)
    {
        var createResult = User.Create(name, email);
        if (createResult.IsFailure)
            return createResult.PropagateError<User>();

        var candidate = createResult.Value;
        candidate.SetLanguage(language);
        candidate.SeedDefaultHandle();
        candidate.BeginSignup(language, referralCode);
        var user = await userStore.UpsertAsync(candidate, cancellationToken);
        return Result.Success(user);
    }
}
