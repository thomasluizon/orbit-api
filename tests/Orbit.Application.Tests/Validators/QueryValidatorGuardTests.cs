using FluentValidation;
using FluentValidation.TestHelper;
using Orbit.Application.ApiKeys.Queries;
using Orbit.Application.ApiKeys.Validators;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Validators;
using Orbit.Application.ChecklistTemplates.Queries;
using Orbit.Application.ChecklistTemplates.Validators;
using Orbit.Application.Gamification.Queries;
using Orbit.Application.Gamification.Validators;
using Orbit.Application.Goals.Queries;
using Orbit.Application.Goals.Validators;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Notifications.Validators;
using Orbit.Application.Profile.Queries;
using Orbit.Application.Profile.Validators;
using Orbit.Application.Referrals.Queries;
using Orbit.Application.Referrals.Validators;
using Orbit.Application.Subscriptions.Queries;
using Orbit.Application.Subscriptions.Validators;
using Orbit.Application.Tags.Queries;
using Orbit.Application.Tags.Validators;
using Orbit.Application.UserFacts.Queries;
using Orbit.Application.UserFacts.Validators;

namespace Orbit.Application.Tests.Validators;

public class QueryValidatorGuardTests
{
    private static void RejectsProperty<T>(AbstractValidator<T> validator, T invalid, string property) =>
        validator.TestValidate(invalid).ShouldHaveValidationErrorFor(property);

    private static void Accepts<T>(AbstractValidator<T> validator, T valid) =>
        validator.TestValidate(valid).ShouldNotHaveAnyValidationErrors();

    [Fact]
    public void GetApiKeys_GuardsUserId()
    {
        var validator = new GetApiKeysQueryValidator();
        RejectsProperty(validator, new GetApiKeysQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetApiKeysQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetCalendarAutoSyncState_GuardsUserId()
    {
        var validator = new GetCalendarAutoSyncStateQueryValidator();
        RejectsProperty(validator, new GetCalendarAutoSyncStateQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetCalendarAutoSyncStateQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetCalendarEvents_GuardsUserId()
    {
        var validator = new GetCalendarEventsQueryValidator();
        RejectsProperty(validator, new GetCalendarEventsQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetCalendarEventsQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetCalendarSyncSuggestions_GuardsUserId()
    {
        var validator = new GetCalendarSyncSuggestionsQueryValidator();
        RejectsProperty(validator, new GetCalendarSyncSuggestionsQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetCalendarSyncSuggestionsQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetUserCalendars_GuardsUserId()
    {
        var validator = new GetUserCalendarsQueryValidator();
        RejectsProperty(validator, new GetUserCalendarsQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetUserCalendarsQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetChecklistTemplates_GuardsUserId()
    {
        var validator = new GetChecklistTemplatesQueryValidator();
        RejectsProperty(validator, new GetChecklistTemplatesQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetChecklistTemplatesQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetAchievements_GuardsUserId()
    {
        var validator = new GetAchievementsQueryValidator();
        RejectsProperty(validator, new GetAchievementsQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetAchievementsQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetGamificationProfile_GuardsUserId()
    {
        var validator = new GetGamificationProfileQueryValidator();
        RejectsProperty(validator, new GetGamificationProfileQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetGamificationProfileQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetStreakInfo_GuardsUserId()
    {
        var validator = new GetStreakInfoQueryValidator();
        RejectsProperty(validator, new GetStreakInfoQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetStreakInfoQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetHabitCount_GuardsUserId()
    {
        var validator = new GetHabitCountQueryValidator();
        RejectsProperty(validator, new GetHabitCountQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetHabitCountQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetHabitWidget_GuardsUserId()
    {
        var validator = new GetHabitWidgetQueryValidator();
        RejectsProperty(validator, new GetHabitWidgetQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetHabitWidgetQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetNotifications_GuardsUserId()
    {
        var validator = new GetNotificationsQueryValidator();
        RejectsProperty(validator, new GetNotificationsQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetNotificationsQuery(Guid.NewGuid()));
    }

    [Fact]
    public void ExportUserData_GuardsUserId()
    {
        var validator = new ExportUserDataQueryValidator();
        RejectsProperty(validator, new ExportUserDataQuery(Guid.Empty), "UserId");
        Accepts(validator, new ExportUserDataQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetProfile_GuardsUserId()
    {
        var validator = new GetProfileQueryValidator();
        RejectsProperty(validator, new GetProfileQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetProfileQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetReferralDashboard_GuardsUserId()
    {
        var validator = new GetReferralDashboardQueryValidator();
        RejectsProperty(validator, new GetReferralDashboardQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetReferralDashboardQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetReferralStats_GuardsUserId()
    {
        var validator = new GetReferralStatsQueryValidator();
        RejectsProperty(validator, new GetReferralStatsQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetReferralStatsQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetBillingDetails_GuardsUserId()
    {
        var validator = new GetBillingDetailsQueryValidator();
        RejectsProperty(validator, new GetBillingDetailsQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetBillingDetailsQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetSubscriptionStatus_GuardsUserId()
    {
        var validator = new GetSubscriptionStatusQueryValidator();
        RejectsProperty(validator, new GetSubscriptionStatusQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetSubscriptionStatusQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetTags_GuardsUserId()
    {
        var validator = new GetTagsQueryValidator();
        RejectsProperty(validator, new GetTagsQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetTagsQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetUserFacts_GuardsUserId()
    {
        var validator = new GetUserFactsQueryValidator();
        RejectsProperty(validator, new GetUserFactsQuery(Guid.Empty), "UserId");
        Accepts(validator, new GetUserFactsQuery(Guid.NewGuid()));
    }

    [Fact]
    public void GetPlans_GuardsUserId()
    {
        var validator = new GetPlansQueryValidator();
        RejectsProperty(validator, new GetPlansQuery(Guid.Empty, null, null), "UserId");
        Accepts(validator, new GetPlansQuery(Guid.NewGuid(), "BR", null));
    }

    [Fact]
    public void GetPublicProfile_GuardsSlug()
    {
        var validator = new GetPublicProfileQueryValidator();
        RejectsProperty(validator, new GetPublicProfileQuery(string.Empty), "Slug");
        Accepts(validator, new GetPublicProfileQuery("astra"));
    }

    [Fact]
    public void GetGoalById_GuardsUserIdAndGoalId()
    {
        var validator = new GetGoalByIdQueryValidator();
        RejectsProperty(validator, new GetGoalByIdQuery(Guid.Empty, Guid.NewGuid()), "UserId");
        RejectsProperty(validator, new GetGoalByIdQuery(Guid.NewGuid(), Guid.Empty), "GoalId");
        Accepts(validator, new GetGoalByIdQuery(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void GetGoalDetail_GuardsUserIdAndGoalId()
    {
        var validator = new GetGoalDetailQueryValidator();
        RejectsProperty(validator, new GetGoalDetailQuery(Guid.Empty, Guid.NewGuid()), "UserId");
        RejectsProperty(validator, new GetGoalDetailQuery(Guid.NewGuid(), Guid.Empty), "GoalId");
        Accepts(validator, new GetGoalDetailQuery(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void GetGoalMetrics_GuardsUserIdAndGoalId()
    {
        var validator = new GetGoalMetricsQueryValidator();
        RejectsProperty(validator, new GetGoalMetricsQuery(Guid.Empty, Guid.NewGuid()), "UserId");
        RejectsProperty(validator, new GetGoalMetricsQuery(Guid.NewGuid(), Guid.Empty), "GoalId");
        Accepts(validator, new GetGoalMetricsQuery(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void GetHabitById_GuardsUserIdAndHabitId()
    {
        var validator = new GetHabitByIdQueryValidator();
        RejectsProperty(validator, new GetHabitByIdQuery(Guid.Empty, Guid.NewGuid()), "UserId");
        RejectsProperty(validator, new GetHabitByIdQuery(Guid.NewGuid(), Guid.Empty), "HabitId");
        Accepts(validator, new GetHabitByIdQuery(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void GetHabitFullDetail_GuardsUserIdAndHabitId()
    {
        var validator = new GetHabitFullDetailQueryValidator();
        RejectsProperty(validator, new GetHabitFullDetailQuery(Guid.Empty, Guid.NewGuid()), "UserId");
        RejectsProperty(validator, new GetHabitFullDetailQuery(Guid.NewGuid(), Guid.Empty), "HabitId");
        Accepts(validator, new GetHabitFullDetailQuery(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void GetHabitLogs_GuardsUserIdAndHabitId()
    {
        var validator = new GetHabitLogsQueryValidator();
        RejectsProperty(validator, new GetHabitLogsQuery(Guid.Empty, Guid.NewGuid()), "UserId");
        RejectsProperty(validator, new GetHabitLogsQuery(Guid.NewGuid(), Guid.Empty), "HabitId");
        Accepts(validator, new GetHabitLogsQuery(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void GetHabitMetrics_GuardsUserIdAndHabitId()
    {
        var validator = new GetHabitMetricsQueryValidator();
        RejectsProperty(validator, new GetHabitMetricsQuery(Guid.Empty, Guid.NewGuid()), "UserId");
        RejectsProperty(validator, new GetHabitMetricsQuery(Guid.NewGuid(), Guid.Empty), "HabitId");
        Accepts(validator, new GetHabitMetricsQuery(Guid.NewGuid(), Guid.NewGuid()));
    }
}
