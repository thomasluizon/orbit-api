using FluentValidation;

namespace Orbit.Application.Auth.Validators;

public static class RefreshTokenRules
{
    public const int TokenLength = 128;

    public static bool IsWellFormed(string? token) =>
        token is { Length: TokenLength }
        && token.All(static character => character is (>= '0' and <= '9') or (>= 'A' and <= 'F'));

    public static void AddRefreshTokenRules<T>(IRuleBuilder<T, string> rule)
    {
        rule
            .NotEmpty()
            .Must(token => IsWellFormed(token))
            .WithMessage("Refresh token format is invalid.");
    }
}
