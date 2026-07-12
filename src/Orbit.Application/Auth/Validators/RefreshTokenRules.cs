using FluentValidation;

namespace Orbit.Application.Auth.Validators;

public static class RefreshTokenRules
{
    public const int TokenLength = 128;
    public const string TokenPattern = "^[0-9A-F]+$";

    public static void AddRefreshTokenRules<T>(IRuleBuilder<T, string> rule)
    {
        rule
            .NotEmpty()
            .Length(TokenLength)
            .Matches(TokenPattern)
            .WithMessage("Refresh token format is invalid.");
    }
}
