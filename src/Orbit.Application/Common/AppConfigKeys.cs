namespace Orbit.Application.Common;

public static class AppConfigKeys
{
    public const string MaxHabitDepth = "MaxHabitDepth";
    public const string MaxTagsPerHabit = "MaxTagsPerHabit";
    public const string ReferralRewardDays = "ReferralRewardDays";
    public const string MaxReferrals = "MaxReferrals";
    public const string FreeMaxHabits = "FreeMaxHabits";
    public const string SubHabitsProOnly = "SubHabitsProOnly";
    public const string FreeAiMessagesPerDay = "FreeAiMessagesPerDay";
    public const string ProAiMessagesPerDay = "ProAiMessagesPerDay";
    public const string FreeAiCallsPerDay = "FreeAiCallsPerDay";
    public const string ProAiCallsPerDay = "ProAiCallsPerDay";
    public const string FreeAiTokensPerDay = "FreeAiTokensPerDay";
    public const string ProAiTokensPerDay = "ProAiTokensPerDay";
    /// <summary>Calls allow tool iterations; token ceilings price every token at the maximum gpt-6-luna rate of $0.50 per million.</summary>
    public const int DefaultFreeAiCallsPerDay = 45;
    public const int DefaultProAiCallsPerDay = 450;
    public const long DefaultFreeAiTokensPerDay = 40_000;
    public const long DefaultProAiTokensPerDay = 240_000;
    public const string DailySummaryProOnly = "DailySummaryProOnly";
    public const string SmartRescheduleProOnly = "SmartRescheduleProOnly";
    public const string RetrospectiveProOnly = "RetrospectiveProOnly";
    public const string MinSupportedVersion = "MinSupportedVersion";
    public const string RequireApiKeyCreationStepUp = "RequireApiKeyCreationStepUp";
    public const string AstraGroundingEnforce = "AstraGroundingEnforce";
}
