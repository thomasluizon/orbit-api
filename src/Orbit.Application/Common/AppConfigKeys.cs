namespace Orbit.Application.Common;

public static class AppConfigKeys
{
    public const string MaxHabitDepth = "MaxHabitDepth";
    public const string MaxTagsPerHabit = "MaxTagsPerHabit";
    public const string ReferralRewardDays = "ReferralRewardDays";
    public const string MaxReferrals = "MaxReferrals";
    public const string FreeMaxHabits = "FreeMaxHabits";
    public const string FreeAiMessagesPerDay = "FreeAiMessagesPerDay";
    public const string ProAiMessagesPerWeek = "ProAiMessagesPerWeek";
    public const string FreeAiCallsPerDay = "FreeAiCallsPerDay";
    public const string ProAiCallsPerWeek = "ProAiCallsPerWeek";
    public const string FreeAiMicroDollarsPerDay = "FreeAiMicroDollarsPerDay";
    public const string ProAiMicroDollarsPerWeek = "ProAiMicroDollarsPerWeek";
    /// <summary>Calls allow tool iterations; the spend backstop is denominated in millionths of a US dollar.</summary>
    public const int DefaultFreeAiCallsPerDay = 110;
    public const int DefaultProAiCallsPerWeek = 5_500;
    public const long DefaultFreeAiMicroDollarsPerDay = 150_000;
    public const long DefaultProAiMicroDollarsPerWeek = 6_000_000;
    public const string DailySummaryProOnly = "DailySummaryProOnly";
    public const string SmartRescheduleProOnly = "SmartRescheduleProOnly";
    public const string RetrospectiveProOnly = "RetrospectiveProOnly";
    public const string MinSupportedVersion = "MinSupportedVersion";
    public const string RequireApiKeyCreationStepUp = "RequireApiKeyCreationStepUp";
    public const string AstraGroundingEnforce = "AstraGroundingEnforce";
}
