using FluentAssertions;
using Orbit.Application.Calendar.Queries;
using NSubstitute;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;


namespace Orbit.Application.Tests.Queries.Calendar;

public class GetCalendarEventsQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo = Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGoogleTokenService _googleTokenService = Substitute.For<IGoogleTokenService>();
    private readonly ICalendarEventFetcher _eventFetcher = Substitute.For<ICalendarEventFetcher>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILogger<GetCalendarEventsQueryHandler> _logger = Substitute.For<ILogger<GetCalendarEventsQueryHandler>>();
    private readonly GetCalendarEventsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetCalendarEventsQueryHandlerTests()
    {
        _payGate.CanAccessCalendar(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _handler = new GetCalendarEventsQueryHandler(
            _userRepo, _habitRepo, _suggestionRepo, _payGate, _googleTokenService, _eventFetcher, _unitOfWork, _logger);
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

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());
        _eventFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>());

        var query = new GetCalendarEventsQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
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

    [Fact]
    public async Task Handle_FiltersOutAlreadyImportedHabitsByGoogleEventId()
    {
        var user = CreateTestUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("valid-access-token");

        var importedHabit = Habit.Create(new HabitCreateParams(
            user.Id, "Existing", Domain.Enums.FrequencyUnit.Week, 1,
            GoogleEventId: "evt_already")).Value;

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { importedHabit }.AsReadOnly());
        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());

        _eventFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<CalendarEventItem>
            {
                new("evt_already", "Existing", null, "2026-05-01", null, null, true, null, []),
                new("evt_new", "Brand New", null, "2026-05-02", null, null, true, null, [])
            });

        var query = new GetCalendarEventsQuery(UserId);
        var result = await _handler.Handle(query, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].Id.Should().Be("evt_new");
    }

    [Fact]
    public async Task Handle_InvalidRefreshToken_MarksReconnectRequiredAndReturnsConnectionFailure()
    {
        var user = CreateTestUser();
        user.SetGoogleTokens("expired-access-token", "refresh-token");

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.TryRefreshAsync(user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome(null, GoogleTokenRefreshResult.RefreshTokenInvalid, "invalid_grant"));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Google Calendar not connected");
        user.GoogleAccessToken.Should().BeNull();
        user.GoogleRefreshToken.Should().BeNull();
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_GoogleApiAuthenticationError_MarksReconnectRequiredAndReturnsReconnectMessage()
    {
        var user = CreateTestUser();
        user.SetGoogleTokens("stale-access-token", null);

        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>())
            .Returns("stale-access-token");

        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());
        _eventFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<DateTime?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<List<CalendarEventItem>>>(_ => throw new CalendarProviderException(
                CalendarFetchErrorKind.ReconnectRequired,
                "Invalid authentication credentials",
                "reconnect required",
                new InvalidOperationException("stub")));

        var result = await _handler.Handle(new GetCalendarEventsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("Google Calendar connection expired. Please reconnect.");
        user.GoogleAccessToken.Should().BeNull();
        user.GoogleRefreshToken.Should().BeNull();
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
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
    public void GetCalendarEventsQuery_RecordEquality()
    {
        var id = Guid.NewGuid();
        var q1 = new GetCalendarEventsQuery(id);
        var q2 = new GetCalendarEventsQuery(id);

        q1.Should().Be(q2);
        q1.UserId.Should().Be(id);
    }

    // BuildReminders tests for the private helper on GoogleCalendarEventFetcher
    // belong in Orbit.Infrastructure.Tests (they require the Google SDK Event types).
    // Application tests intentionally stay SDK-free after the Calendar extraction.
}
