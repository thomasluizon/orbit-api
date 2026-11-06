namespace Orbit.Application.Auth.Services;

/// <summary>
/// Reads the fixed production sign-in codes of the release smoke account (<c>SMOKE_TEST_EMAIL</c>,
/// <c>SMOKE_TEST_CODE</c>) and the store-review account (<c>REVIEW_ACCOUNT_EMAIL</c>,
/// <c>REVIEW_ACCOUNT_CODE</c>). A pair applies only when its address matches and its code is exactly six
/// ASCII digits. Callers apply the production gate.
/// </summary>
public static class ProductionSignInCode
{
    public static string? For(string email)
        => Read(email, "SMOKE_TEST_EMAIL", "SMOKE_TEST_CODE") ?? ForStoreReview(email);

    public static string? ForStoreReview(string email)
        => Read(email, "REVIEW_ACCOUNT_EMAIL", "REVIEW_ACCOUNT_CODE");

    private static string? Read(string email, string emailVariable, string codeVariable)
    {
        var configuredEmail = Environment.GetEnvironmentVariable(emailVariable)?.Trim();
        var code = Environment.GetEnvironmentVariable(codeVariable);
        return code is { Length: 6 } && code.All(char.IsAsciiDigit)
            && !string.IsNullOrEmpty(configuredEmail)
            && string.Equals(configuredEmail, email.Trim(), StringComparison.OrdinalIgnoreCase) ? code : null;
    }
}
