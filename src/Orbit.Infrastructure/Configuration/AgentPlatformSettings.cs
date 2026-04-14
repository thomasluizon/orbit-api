namespace Orbit.Infrastructure.Configuration;

public sealed class AgentPlatformSettings
{
    public const string SectionName = "AgentPlatform";

    public bool ShadowModeEnabled { get; init; }
    public int PendingOperationTtlMinutes { get; init; } = 10;
    public int StepUpChallengeTtlMinutes { get; init; } = 5;
    public int StepUpChallengeCooldownSeconds { get; init; } = 60;
    public int StepUpMaxAttempts { get; init; } = 5;
}
