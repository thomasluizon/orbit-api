using System.Text.Json;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Notifications.Commands;
using Orbit.Application.Notifications.Queries;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Chat.Tools;

public class ProfileNotificationCalendarToolTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public void ToolMetadata_ExposesNamesAndSchemas()
    {
        var mediator = Substitute.For<IMediator>();
        var profileTool = new GetProfileTool(mediator);
        var preferencesTool = new UpdateProfilePreferencesTool(mediator);
        var aiSettingsTool = new UpdateAiSettingsTool(mediator);
        var notificationsTool = new GetNotificationsTool(mediator);
        var updateNotificationsTool = new UpdateNotificationsTool(mediator);
        var deleteNotificationsTool = new DeleteNotificationsTool(mediator);
        var calendarOverviewTool = new GetCalendarOverviewTool(mediator);
        var calendarSyncTool = new ManageCalendarSyncTool(mediator);

        profileTool.Name.Should().Be("get_profile");
        profileTool.IsReadOnly.Should().BeTrue();
        JsonSerializer.Serialize(profileTool.GetParameterSchema()).Should().Contain("properties");

        preferencesTool.Name.Should().Be("update_profile_preferences");
        JsonSerializer.Serialize(preferencesTool.GetParameterSchema()).Should().Contain("set_theme_preference");
        JsonSerializer.Serialize(preferencesTool.GetParameterSchema()).Should().Contain("color_scheme");

        aiSettingsTool.Name.Should().Be("update_ai_settings");
        JsonSerializer.Serialize(aiSettingsTool.GetParameterSchema()).Should().Contain("set_ai_summary");

        notificationsTool.Name.Should().Be("get_notifications");
        notificationsTool.IsReadOnly.Should().BeTrue();
        JsonSerializer.Serialize(notificationsTool.GetParameterSchema()).Should().Contain("properties");

        updateNotificationsTool.Name.Should().Be("update_notifications");
        JsonSerializer.Serialize(updateNotificationsTool.GetParameterSchema()).Should().Contain("subscribe_push");

        deleteNotificationsTool.Name.Should().Be("delete_notifications");
        JsonSerializer.Serialize(deleteNotificationsTool.GetParameterSchema()).Should().Contain("delete_all");

        calendarOverviewTool.Name.Should().Be("get_calendar_overview");
        calendarOverviewTool.IsReadOnly.Should().BeTrue();
        JsonSerializer.Serialize(calendarOverviewTool.GetParameterSchema()).Should().Contain("include_auto_sync_state");

        calendarSyncTool.Name.Should().Be("manage_calendar_sync");
        JsonSerializer.Serialize(calendarSyncTool.GetParameterSchema()).Should().Contain("dismiss_suggestion");
    }

    [Fact]
    public async Task GetProfileTool_ReturnsSuccessPayload()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<ProfileResponse>(null!));
        var tool = new GetProfileTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public async Task GetProfileTool_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetProfileQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<ProfileResponse>("user_not_found"));
        var tool = new GetProfileTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("user_not_found");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_RequiresAction()
    {
        var tool = new UpdateProfilePreferencesTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("action is required.");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_SetsTimezone()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetTimezoneCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateProfilePreferencesTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_timezone","timezone":"America/New_York"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Timezone set to America/New_York");
        await mediator.Received(1).Send(
            Arg.Is<SetTimezoneCommand>(command => command.UserId == UserId && command.TimeZone == "America/New_York"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_RequiresTimezone()
    {
        var tool = new UpdateProfilePreferencesTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_timezone"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("timezone is required.");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_SetsLanguage()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetLanguageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateProfilePreferencesTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_language","language":"pt-BR"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Language set to pt-BR");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_RequiresWeekStartDay()
    {
        var tool = new UpdateProfilePreferencesTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_week_start_day"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("week_start_day is required.");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_RequiresThemePreferenceProperty()
    {
        var tool = new UpdateProfilePreferencesTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_theme_preference"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("theme_preference is required.");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_SetsThemePreference()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetThemePreferenceCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateProfilePreferencesTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"action":"set_theme_preference","theme_preference":"dark"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Theme preference updated");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_RequiresColorSchemeProperty()
    {
        var tool = new UpdateProfilePreferencesTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_color_scheme"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("color_scheme is required.");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_SetsColorScheme()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetColorSchemeCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateProfilePreferencesTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"action":"set_color_scheme","color_scheme":"sunset"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Color scheme updated");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_CompletesOnboarding()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CompleteOnboardingCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateProfilePreferencesTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"complete_onboarding"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Onboarding completed");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_CompletesTour()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CompleteTourCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateProfilePreferencesTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"complete_tour"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Tour completed");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_ResetsTour()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ResetTourCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateProfilePreferencesTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"reset_tour"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Tour reset");
    }

    [Fact]
    public async Task UpdateProfilePreferencesTool_ReturnsFailureForUnsupportedAction()
    {
        var tool = new UpdateProfilePreferencesTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"unknown"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unsupported action 'unknown'.");
    }

    [Fact]
    public async Task UpdateAiSettingsTool_RequiresEnabled()
    {
        var tool = new UpdateAiSettingsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_ai_memory"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("action and enabled are required.");
    }

    [Fact]
    public async Task UpdateAiSettingsTool_BuildsAiMemoryEntityName()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetAiMemoryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateAiSettingsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_ai_memory","enabled":true}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("AI memory enabled");
    }

    [Fact]
    public async Task UpdateAiSettingsTool_BuildsAiSummaryDisabledEntityName()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetAiSummaryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateAiSettingsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_ai_summary","enabled":false}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("AI summary disabled");
    }

    [Fact]
    public async Task UpdateAiSettingsTool_PropagatesMediatorFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetAiSummaryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("summary_failed"));
        var tool = new UpdateAiSettingsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_ai_summary","enabled":true}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("summary_failed");
    }

    [Fact]
    public async Task UpdateAiSettingsTool_ReturnsFailureForUnsupportedAction()
    {
        var tool = new UpdateAiSettingsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"unknown","enabled":false}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unsupported action 'unknown'.");
    }

    [Fact]
    public async Task GetNotificationsTool_ReturnsSuccessPayload()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GetNotificationsResponse([], 0)));
        var tool = new GetNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetNotificationsTool_ReturnsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetNotificationsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<GetNotificationsResponse>("notifications_failed"));
        var tool = new GetNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("notifications_failed");
    }

    [Fact]
    public async Task UpdateNotificationsTool_RequiresAction()
    {
        var tool = new UpdateNotificationsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("action is required.");
    }

    [Fact]
    public async Task UpdateNotificationsTool_RejectsInvalidNotificationId()
    {
        var tool = new UpdateNotificationsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"mark_read","notification_id":"bad"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("notification_id must be a valid GUID.");
    }

    [Fact]
    public async Task UpdateNotificationsTool_MarksNotificationRead()
    {
        var notificationId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<MarkNotificationReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse($$"""{"action":"mark_read","notification_id":"{{notificationId}}"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(notificationId.ToString());
        result.EntityName.Should().Be("Marked notification as read");
    }

    [Fact]
    public async Task UpdateNotificationsTool_MarksAllNotificationsRead()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<MarkAllNotificationsReadCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"mark_all_read"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Marked all notifications as read");
    }

    [Fact]
    public async Task UpdateNotificationsTool_RequiresSubscriptionFields()
    {
        var tool = new UpdateNotificationsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"subscribe_push","endpoint":"https://push"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("endpoint, p256dh, and auth are required.");
    }

    [Fact]
    public async Task UpdateNotificationsTool_SubscribesPush()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SubscribePushCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"action":"subscribe_push","endpoint":"https://push","p256dh":"key","auth":"secret"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Push subscription registered");
    }

    [Fact]
    public async Task UpdateNotificationsTool_UnsubscribesPush()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UnsubscribePushCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new UpdateNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"action":"unsubscribe_push","endpoint":"https://push"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Push subscription removed");
    }

    [Fact]
    public async Task UpdateNotificationsTool_RequiresEndpointForUnsubscribe()
    {
        var tool = new UpdateNotificationsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"unsubscribe_push"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("endpoint is required.");
    }

    [Fact]
    public async Task UpdateNotificationsTool_HandlesTestPushFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<TestPushNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<TestPushNotificationResponse>("push_failed"));
        var tool = new UpdateNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"test_push"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("push_failed");
    }

    [Fact]
    public async Task UpdateNotificationsTool_ReturnsTestPushSuccess()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<TestPushNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new TestPushNotificationResponse(2, "sent")));
        var tool = new UpdateNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"test_push"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Test push requested");
    }

    [Fact]
    public async Task UpdateNotificationsTool_ReturnsFailureForUnsupportedAction()
    {
        var tool = new UpdateNotificationsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"unknown"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unsupported action 'unknown'.");
    }

    [Fact]
    public async Task DeleteNotificationsTool_RequiresAction()
    {
        var tool = new DeleteNotificationsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("action is required.");
    }

    [Fact]
    public async Task DeleteNotificationsTool_DeletesAll()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteAllNotificationsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new DeleteNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"delete_all"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Deleted all notifications");
    }

    [Fact]
    public async Task DeleteNotificationsTool_DeletesOne()
    {
        var notificationId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteNotificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new DeleteNotificationsTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse($$"""{"action":"delete_one","notification_id":"{{notificationId}}"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(notificationId.ToString());
        result.EntityName.Should().Be("Deleted notification");
    }

    [Fact]
    public async Task DeleteNotificationsTool_RejectsInvalidNotificationId()
    {
        var tool = new DeleteNotificationsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"delete_one","notification_id":"bad"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("notification_id must be a valid GUID.");
    }

    [Fact]
    public async Task DeleteNotificationsTool_ReturnsFailureForUnsupportedAction()
    {
        var tool = new DeleteNotificationsTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"unknown"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unsupported action 'unknown'.");
    }

    [Fact]
    public async Task GetCalendarOverviewTool_ReturnsOverview()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarEventItem>()));
        mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CalendarAutoSyncStateResponse(true, GoogleCalendarAutoSyncStatus.Idle, null, true)));
        mediator.Send(Arg.Any<GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarSyncSuggestionItem>()));
        var tool = new GetCalendarOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GetCalendarOverviewTool_StopsOnEventsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<List<CalendarEventItem>>("events_failed"));
        var tool = new GetCalendarOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("events_failed");
    }

    [Fact]
    public async Task GetCalendarOverviewTool_StopsOnAutoSyncFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarEventItem>()));
        mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CalendarAutoSyncStateResponse>("auto_sync_failed"));
        var tool = new GetCalendarOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("auto_sync_failed");
    }

    [Fact]
    public async Task GetCalendarOverviewTool_StopsOnSuggestionsFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new List<CalendarEventItem>()));
        mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CalendarAutoSyncStateResponse(true, GoogleCalendarAutoSyncStatus.Idle, null, true)));
        mediator.Send(Arg.Any<GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<List<CalendarSyncSuggestionItem>>("suggestions_failed"));
        var tool = new GetCalendarOverviewTool(mediator);

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("suggestions_failed");
    }

    [Fact]
    public async Task GetCalendarOverviewTool_SkipsQueriesWhenFlagsAreFalse()
    {
        var mediator = Substitute.For<IMediator>();
        var tool = new GetCalendarOverviewTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse("""{"include_events":false,"include_auto_sync_state":false,"include_suggestions":false}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        await mediator.DidNotReceiveWithAnyArgs().Send(default!, default);
    }

    [Fact]
    public async Task ManageCalendarSyncTool_RequiresAction()
    {
        var tool = new ManageCalendarSyncTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("{}"), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("action is required.");
    }

    [Fact]
    public async Task ManageCalendarSyncTool_RequiresEnabledForAutoSync()
    {
        var tool = new ManageCalendarSyncTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_auto_sync"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("enabled is required.");
    }

    [Fact]
    public async Task ManageCalendarSyncTool_SetsAutoSync()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new ManageCalendarSyncTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"set_auto_sync","enabled":true}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Calendar auto-sync enabled");
    }

    [Fact]
    public async Task ManageCalendarSyncTool_RejectsInvalidSuggestionId()
    {
        var tool = new ManageCalendarSyncTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"dismiss_suggestion","suggestion_id":"bad"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("suggestion_id must be a valid GUID.");
    }

    [Fact]
    public async Task ManageCalendarSyncTool_DismissesSuggestion()
    {
        var suggestionId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DismissCalendarSuggestionCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new ManageCalendarSyncTool(mediator);

        var result = await tool.ExecuteAsync(
            Parse($$"""{"action":"dismiss_suggestion","suggestion_id":"{{suggestionId}}"}"""),
            UserId,
            CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityId.Should().Be(suggestionId.ToString());
    }

    [Fact]
    public async Task ManageCalendarSyncTool_DismissesImport()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DismissCalendarImportCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var tool = new ManageCalendarSyncTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"dismiss_import"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Dismissed calendar import prompt");
    }

    [Fact]
    public async Task ManageCalendarSyncTool_RunsSync()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<RunCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new CalendarAutoSyncResult(1, 2, GoogleCalendarAutoSyncStatus.Idle)));
        var tool = new ManageCalendarSyncTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"run_sync"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Calendar sync requested");
    }

    [Fact]
    public async Task ManageCalendarSyncTool_HandlesRunSyncFailure()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<RunCalendarAutoSyncCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CalendarAutoSyncResult>("sync_failed"));
        var tool = new ManageCalendarSyncTool(mediator);

        var result = await tool.ExecuteAsync(Parse("""{"action":"run_sync"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("sync_failed");
    }

    [Fact]
    public async Task ManageCalendarSyncTool_ReturnsFailureForUnsupportedAction()
    {
        var tool = new ManageCalendarSyncTool(Substitute.For<IMediator>());

        var result = await tool.ExecuteAsync(Parse("""{"action":"unknown"}"""), UserId, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Unsupported action 'unknown'.");
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
