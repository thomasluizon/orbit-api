namespace Orbit.Domain.Entities;

public partial class User
{
    public string? SignupLanguage { get; private set; }
    public string? SignupReferralCode { get; private set; }
    public DateTime? SignupCompletedAtUtc { get; private set; }
    public DateTime? SignupReferralCompletedAtUtc { get; private set; }
    public DateTime? SignupAnalyticsCompletedAtUtc { get; private set; }
    public DateTime? SignupWelcomeCompletedAtUtc { get; private set; }

    /// <summary>
    /// Retains the original signup inputs across later sign-ins. A null language identifies
    /// accounts created before durable signup tracking, which must not replay historical signup work.
    /// </summary>
    public void BeginSignup(string language, string? referralCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        if (language.Length > 10)
            throw new ArgumentOutOfRangeException(nameof(language));

        if (SignupLanguage is not null)
            return;

        SignupLanguage = language;
        SignupReferralCode = referralCode;
    }

    public void CompleteSignupReferral()
    {
        EnsureSignupStarted();
        SignupReferralCompletedAtUtc ??= DateTime.UtcNow;
        CompleteSignupIfReady();
    }

    public void CompleteSignupAnalytics()
    {
        EnsureSignupStarted();
        SignupAnalyticsCompletedAtUtc ??= DateTime.UtcNow;
        CompleteSignupIfReady();
    }

    public void CompleteSignupWelcome()
    {
        EnsureSignupStarted();
        SignupWelcomeCompletedAtUtc ??= DateTime.UtcNow;
        CompleteSignupIfReady();
    }

    private void EnsureSignupStarted()
    {
        if (SignupLanguage is null)
            throw new InvalidOperationException("Signup must be started before recording completion.");
    }

    private void CompleteSignupIfReady()
    {
        if (SignupReferralCompletedAtUtc is not null && SignupAnalyticsCompletedAtUtc is not null
            && SignupWelcomeCompletedAtUtc is not null)
            SignupCompletedAtUtc ??= DateTime.UtcNow;
    }
}
