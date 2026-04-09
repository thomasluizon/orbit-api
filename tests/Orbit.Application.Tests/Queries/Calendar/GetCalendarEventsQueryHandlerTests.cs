using FluentAssertions;
using Google.Apis.Calendar.v3.Data;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;

namespace Orbit.Application.Tests.Queries.Calendar;

public class GetCalendarEventsQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGoogleTokenService _googleTokenService = Substitute.For<IGoogleTokenService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILogger<GetCalendarEventsQueryHandler> _logger = Substitute.For<ILogger<GetCalendarEventsQueryHandler>>();
    private readonly GetCalendarEventsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetCalendarEventsQueryHandlerTests()
    {
        _handler = new GetCalendarEventsQueryHandler(_userRepo, _habitRepo, _googleTokenService, _unitOfWork, _logger);
    }

    private static User CreateTestUser()
    {
        return User.Create("Test User", "test@example.com").Value;
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetCalendarEventsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
        result.ErrorCode.Should().Be("USER_NOT_FOUND");
    }

    [Fact]
    public async Task Handle_NoGoogleToken_ReturnsFailure()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns((string?)null);

        var query = new GetCalendarEventsQuery(UserId);

        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Google Calendar not connected");
    }

    [Fact]
    public async Task Handle_UserNotFound_UsesCorrectErrorCode()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetCalendarEventsQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.UserNotFound);
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_NoGoogleToken_DoesNotSaveChanges()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns((string?)null);

        var query = new GetCalendarEventsQuery(UserId);
        await _handler.Handle(query, CancellationToken.None);

        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_ValidToken_PersistsRefreshedToken()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("valid-access-token");

        // Setup habits repo for the filter (empty)
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());

        var query = new GetCalendarEventsQuery(UserId);

        // This will fail at Google API call (since we can't mock the CalendarService),
        // but we can verify SaveChanges was called before the Google API call.
        // The handler calls SaveChangesAsync right after getting the token.
        try
        {
            await _handler.Handle(query, CancellationToken.None);
        }
        catch
        {
            // Expected: Google API call will fail in test environment
        }

        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UserNotFound_DoesNotCallTokenService()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var query = new GetCalendarEventsQuery(UserId);
        await _handler.Handle(query, CancellationToken.None);

        await _googleTokenService.DidNotReceive()
            .GetValidAccessTokenAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoGoogleToken_ErrorMessageGuidesUser()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns((string?)null);

        var query = new GetCalendarEventsQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.Error.Should().Contain("sign in with Google");
    }

    // --- CalendarEventItem record tests ---

    [Fact]
    public void CalendarEventItem_Properties_SetCorrectly()
    {
        var item = new CalendarEventItem(
            "evt_123", "Team Meeting", "Weekly sync",
            "2026-04-03", "14:00", "15:00",
            true, "RRULE:FREQ=WEEKLY;BYDAY=FR",
            [15, 30]);

        item.Id.Should().Be("evt_123");
        item.Title.Should().Be("Team Meeting");
        item.Description.Should().Be("Weekly sync");
        item.StartDate.Should().Be("2026-04-03");
        item.StartTime.Should().Be("14:00");
        item.EndTime.Should().Be("15:00");
        item.IsRecurring.Should().BeTrue();
        item.RecurrenceRule.Should().Be("RRULE:FREQ=WEEKLY;BYDAY=FR");
        item.Reminders.Should().Equal(15, 30);
    }

    [Fact]
    public void CalendarEventItem_NonRecurring_PropertiesCorrect()
    {
        var item = new CalendarEventItem(
            "evt_456", "Doctor Appointment", null,
            "2026-04-10", "09:00", "10:00",
            false, null, []);

        item.IsRecurring.Should().BeFalse();
        item.RecurrenceRule.Should().BeNull();
        item.Description.Should().BeNull();
        item.Reminders.Should().BeEmpty();
    }

    [Fact]
    public void CalendarEventItem_AllDayEvent_NoTimes()
    {
        var item = new CalendarEventItem(
            "evt_789", "Holiday", null,
            "2026-04-25", null, null,
            false, null, []);

        item.StartTime.Should().BeNull();
        item.EndTime.Should().BeNull();
    }

    // --- GetCalendarEventsQuery record test ---

    [Fact]
    public void GetCalendarEventsQuery_RecordEquality()
    {
        var id = Guid.NewGuid();
        var q1 = new GetCalendarEventsQuery(id);
        var q2 = new GetCalendarEventsQuery(id);

        q1.Should().Be(q2);
        q1.UserId.Should().Be(id);
    }

    [Fact]
    public void BuildReminders_TimedEventWithoutExplicitReminders_AddsDefaultAndAtTime()
    {
        var result = GetCalendarEventsQueryHandler.BuildReminders(new Event(), "09:00");

        result.Should().Equal(AppConstants.DefaultReminderMinutes, 0);
    }

    [Fact]
    public void BuildReminders_TimedEventWithExplicitReminders_PreservesThemAndAddsAtTime()
    {
        var ev = new Event
        {
            Reminders = new Event.RemindersData
            {
                Overrides =
                [
                    new EventReminder { Minutes = 30 },
                    new EventReminder { Minutes = 15 }
                ]
            }
        };

        var result = GetCalendarEventsQueryHandler.BuildReminders(ev, "09:00");

        result.Should().Equal(30, 15, 0);
    }

    [Fact]
    public void BuildReminders_TimedEventWithExistingAtTime_DoesNotDuplicateZero()
    {
        var ev = new Event
        {
            Reminders = new Event.RemindersData
            {
                Overrides =
                [
                    new EventReminder { Minutes = 15 },
                    new EventReminder { Minutes = 0 },
                    new EventReminder { Minutes = 15 }
                ]
            }
        };

        var result = GetCalendarEventsQueryHandler.BuildReminders(ev, "09:00");

        result.Should().Equal(15, 0);
    }

    [Fact]
    public void BuildReminders_AllDayEventWithoutExplicitReminders_RemainsEmpty()
    {
        var result = GetCalendarEventsQueryHandler.BuildReminders(new Event(), null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildReminders_AllDayEventWithExplicitReminders_PreservesThem()
    {
        var ev = new Event
        {
            Reminders = new Event.RemindersData
            {
                Overrides =
                [
                    new EventReminder { Minutes = 60 }
                ]
            }
        };

        var result = GetCalendarEventsQueryHandler.BuildReminders(ev, null);

        result.Should().Equal(60);
    }
}
