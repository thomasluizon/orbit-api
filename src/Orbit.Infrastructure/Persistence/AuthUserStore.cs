using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Infrastructure.Persistence;

/// <summary>
/// Uses an email-keyed upsert so returning accounts also incur a write. A conflicting candidate
/// never replaces profile, subscription, trial, language, or handle values on the stored account.
/// </summary>
public sealed class AuthUserStore(OrbitDbContext context) : IAuthUserStore
{
    public async Task<User> UpsertAsync(User candidate, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Users" (
                "Id", "Name", "Email", "AiMemoryEnabled",
                "AiSummaryEnabled", "ProactiveAstraEnabled", "HasCompletedOnboarding", "HasCompletedTour",
                "HasCreatedFirstHabit", "HasLoggedFirstHabit", "HasTriedAstra", "HasCompletedOnboardingChecklist",
                "Plan", "IsLifetimePro", "AiMessagesUsedToday", "CreatedAtUtc",
                "HasImportedCalendar", "HasSeenImportPrompt", "GoogleCalendarAutoSyncEnabled", "IsDeactivated",
                "WeekStartDay", "ReminderPreferencesVersion", "SocialOptIn", "IsAdmin",
                "TotalXp", "Level", "AdRewardBonusMessages", "AdRewardsClaimedToday",
                "CurrentStreak", "LongestStreak", "StreakFreezesAccumulated", "LastFreezeAwardStreak",
                "PublicProfileShowStreak", "PublicProfileShowLevel", "PublicProfileShowAchievements", "PublicProfileShowTopHabits",
                "Language", "TrialEndsAt", "SignupLanguage", "SignupReferralCode")
            VALUES (
                {candidate.Id}, {candidate.Name}, {candidate.Email}, {candidate.AiMemoryEnabled},
                {candidate.AiSummaryEnabled}, {candidate.ProactiveAstraEnabled}, {candidate.HasCompletedOnboarding}, {candidate.HasCompletedTour},
                {candidate.HasCreatedFirstHabit}, {candidate.HasLoggedFirstHabit}, {candidate.HasTriedAstra}, {candidate.HasCompletedOnboardingChecklist},
                {(int)candidate.SubscriptionPlan}, {candidate.IsLifetimePro}, {candidate.AiMessagesUsedToday}, {candidate.CreatedAtUtc},
                {candidate.HasImportedCalendar}, {candidate.HasSeenImportPrompt}, {candidate.GoogleCalendarAutoSyncEnabled}, {candidate.IsDeactivated},
                {candidate.WeekStartDay}, {candidate.ReminderPreferencesVersion}, {candidate.SocialOptIn}, {candidate.IsAdmin},
                {candidate.TotalXp}, {candidate.Level}, {candidate.AdRewardBonusMessages}, {candidate.AdRewardsClaimedToday},
                {candidate.CurrentStreak}, {candidate.LongestStreak}, {candidate.StreakFreezesAccumulated}, {candidate.LastFreezeAwardStreak},
                {candidate.PublicProfileShowStreak}, {candidate.PublicProfileShowLevel}, {candidate.PublicProfileShowAchievements}, {candidate.PublicProfileShowTopHabits},
                {candidate.Language}, {candidate.TrialEndsAt}, {candidate.SignupLanguage}, {candidate.SignupReferralCode})
            ON CONFLICT ("Email") DO UPDATE SET "Email" = excluded."Email"
            """, cancellationToken);

        var tracked = context.Users.Local.SingleOrDefault(u => u.Email == candidate.Email);
        if (tracked is not null)
            context.Entry(tracked).State = EntityState.Detached;

        var user = await context.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Email == candidate.Email, cancellationToken);
        context.Entry(user).Property(u => u.Email).IsModified = true;
        return user;
    }
}
