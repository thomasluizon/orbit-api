namespace Orbit.Infrastructure.Configuration;

public sealed class AuthCacheSettings
{
    public const string SectionName = "AuthCache";
    public int ApiKeyTtlSeconds { get; init; } = 30;
    public int SessionTtlSeconds { get; init; } = 15;

    public void Validate()
    {
        if (ApiKeyTtlSeconds is < 0 or > 60 || SessionTtlSeconds is < 0 or > 60)
            throw new InvalidOperationException("Auth cache lifetimes must be between zero and 60 seconds.");
    }
}
