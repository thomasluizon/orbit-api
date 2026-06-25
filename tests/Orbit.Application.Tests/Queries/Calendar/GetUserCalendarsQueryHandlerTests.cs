using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Calendar;

public class GetUserCalendarsQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IGoogleTokenService _googleTokenService = Substitute.For<IGoogleTokenService>();
    private readonly ICalendarEventFetcher _eventFetcher = Substitute.For<ICalendarEventFetcher>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ILogger<GetUserCalendarsQueryHandler> _logger = Substitute.For<ILogger<GetUserCalendarsQueryHandler>>();
    private readonly GetUserCalendarsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetUserCalendarsQueryHandlerTests()
    {
        _payGate.CanAccessCalendar(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));
        _handler = new GetUserCalendarsQueryHandler(
            _userRepo, _payGate, _googleTokenService, _eventFetcher, _unitOfWork, _logger);
    }

    private static User CreateUser() => User.Create("Test", "test@example.com").Value;

    private static List<CalendarListItem> SampleCalendars() =>
    [
        new("owned", "Rotina", "owner", true, "#fff", IsDefaultOwned: true),
        new("shared", "Team", "reader", false, "#abc", IsDefaultOwned: false)
    ];

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _handler.Handle(new GetUserCalendarsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.UserNotFound);
    }

    [Fact]
    public async Task Handle_NotConnected_ReturnsCalendarNotConnected()
    {
        var user = CreateUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns((string?)null);

        var result = await _handler.Handle(new GetUserCalendarsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CalendarNotConnected);
    }

    [Fact]
    public async Task Handle_NullSelection_IsSyncedReflectsDefaultOwned()
    {
        var user = CreateUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns("token");
        _eventFetcher.ListCalendarsAsync("token", Arg.Any<CancellationToken>()).Returns(SampleCalendars());

        var result = await _handler.Handle(new GetUserCalendarsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Single(c => c.Id == "owned").IsSynced.Should().BeTrue();
        result.Value.Single(c => c.Id == "shared").IsSynced.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_ExplicitSelection_IsSyncedReflectsSelectedSet()
    {
        var user = CreateUser();
        user.SetSelectedCalendars(new[] { "shared" });
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns("token");
        _eventFetcher.ListCalendarsAsync("token", Arg.Any<CancellationToken>()).Returns(SampleCalendars());

        var result = await _handler.Handle(new GetUserCalendarsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Single(c => c.Id == "owned").IsSynced.Should().BeFalse();
        result.Value.Single(c => c.Id == "shared").IsSynced.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_MapsAllReturnedFields()
    {
        var user = CreateUser();
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns("token");
        _eventFetcher.ListCalendarsAsync("token", Arg.Any<CancellationToken>()).Returns(SampleCalendars());

        var result = await _handler.Handle(new GetUserCalendarsQuery(UserId), CancellationToken.None);

        var owned = result.Value.Single(c => c.Id == "owned");
        owned.Name.Should().Be("Rotina");
        owned.AccessRole.Should().Be("owner");
        owned.Primary.Should().BeTrue();
        owned.BackgroundColor.Should().Be("#fff");
    }

    [Fact]
    public async Task Handle_ReconnectRequired_MarksUserAndReturnsReconnectMessage()
    {
        var user = CreateUser();
        user.SetGoogleTokens("stale", null);
        _userRepo.GetByIdAsync(UserId, Arg.Any<CancellationToken>()).Returns(user);
        _googleTokenService.GetValidAccessTokenAsync(user, Arg.Any<CancellationToken>()).Returns("stale");
        _eventFetcher.ListCalendarsAsync("stale", Arg.Any<CancellationToken>())
            .Returns<Task<List<CalendarListItem>>>(_ => throw new CalendarProviderException(
                CalendarFetchErrorKind.ReconnectRequired, "invalid_grant", "boom", new Exception()));

        var result = await _handler.Handle(new GetUserCalendarsQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(ErrorCodes.CalendarReconnectRequired);
        user.GoogleAccessToken.Should().BeNull();
    }
}
