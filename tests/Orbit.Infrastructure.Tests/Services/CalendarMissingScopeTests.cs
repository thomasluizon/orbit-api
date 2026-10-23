using System.Linq.Expressions;
using FluentAssertions;
using Google.Apis.Calendar.v3.Data;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar.Commands;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Calendar;

namespace Orbit.Infrastructure.Tests.Services;

public class CalendarMissingScopeTests
{
    private readonly IGoogleCalendarApi _api = Substitute.For<IGoogleCalendarApi>();
    private readonly IGenericRepository<User> _users = Substitute.For<IGenericRepository<User>>();
    private readonly IGoogleTokenService _tokens = Substitute.For<IGoogleTokenService>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly User _user = User.Create("Test", "test@example.com").Value;
    private readonly GoogleCalendarEventFetcher _fetcher;

    public CalendarMissingScopeTests()
    {
        _user.SetGoogleTokens("token", "refresh");
        _user.EnableCalendarAutoSync();
        _users.GetByIdAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(_user);
        _users.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>()).Returns(_user);
        _tokens.GetValidAccessTokenAsync(_user, Arg.Any<CancellationToken>()).Returns("token");
        _tokens.TryRefreshAsync(_user, Arg.Any<CancellationToken>())
            .Returns(new GoogleTokenRefreshOutcome("token", GoogleTokenRefreshResult.Success, null));
        _payGate.CanAccessCalendar(_user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _payGate.CanManageCalendar(_user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        _fetcher = new GoogleCalendarEventFetcher(_api, new RecordingLogger<GoogleCalendarEventFetcher>());
    }

    [Fact]
    public async Task CalendarList_MissingScope_ReturnsExistingReconnectResultAndWarns()
    {
        StubFailure("list");
        var logger = new RecordingLogger<GetUserCalendarsQueryHandler>();
        var handler = new GetUserCalendarsQueryHandler(_users, _payGate, _tokens, _fetcher, _unitOfWork, logger);

        var result = await handler.Handle(new GetUserCalendarsQuery(_user.Id), default);

        result.ErrorCode.Should().Be(ErrorCodes.CalendarReconnectRequired);
        AssertReconnectState(logger.Levels);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("list")]
    [InlineData("events")]
    [InlineData("master")]
    public async Task CalendarEvents_MissingScope_ReturnsExistingReconnectResultAndWarns(string operation)
    {
        StubFailure(operation);
        var logger = new RecordingLogger<GetCalendarEventsQueryHandler>();
        var repositories = new GetCalendarEventsRepositories(_users, Substitute.For<IGenericRepository<Habit>>(),
            Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>());
        var handler = new GetCalendarEventsQueryHandler(repositories, _payGate, _tokens, _fetcher, _unitOfWork, logger);

        var result = await handler.Handle(new GetCalendarEventsQuery(_user.Id), default);

        result.ErrorCode.Should().Be(ErrorCodes.CalendarReconnectRequired);
        AssertReconnectState(logger.Levels);
        await _unitOfWork.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("list", false)]
    [InlineData("events", false)]
    [InlineData("master", false)]
    [InlineData("events", true)]
    public async Task AutoSync_MissingScope_PausesSyncAndDoesNotRetry(string operation, bool opportunistic)
    {
        StubFailure(operation);
        var syncLock = Substitute.For<ICalendarSyncLock>();
        syncLock.TryAcquireAsync(_user.Id, Arg.Any<CancellationToken>()).Returns(true);
        var notifications = Substitute.For<IGenericRepository<Notification>>();
        var dependencies = new CalendarAutoSyncDependencies(_users, Substitute.For<IGenericRepository<Habit>>(),
            Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>(), notifications,
            _tokens, _fetcher, _unitOfWork, syncLock);
        var logger = new RecordingLogger<RunCalendarAutoSyncCommandHandler>();
        var handler = new RunCalendarAutoSyncCommandHandler(dependencies, _payGate, TimeProvider.System, logger);

        var result = await handler.Handle(new RunCalendarAutoSyncCommand(_user.Id, opportunistic), default);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(GoogleCalendarAutoSyncStatus.ReconnectRequired);
        AssertReconnectState(logger.Levels);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await notifications.Received(1).AddAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>());
        _api.ClearReceivedCalls();
        _tokens.ClearReceivedCalls();

        await handler.Handle(new RunCalendarAutoSyncCommand(_user.Id, opportunistic), default);

        _api.ReceivedCalls().Should().BeEmpty();
        _tokens.ReceivedCalls().Should().BeEmpty();
        await notifications.Received(1).AddAsync(Arg.Any<Notification>(), Arg.Any<CancellationToken>());
    }

    private void AssertReconnectState(List<LogLevel> levels)
    {
        _user.GoogleCalendarAutoSyncEnabled.Should().BeFalse();
        _user.GoogleCalendarAutoSyncStatus.Should().Be(GoogleCalendarAutoSyncStatus.ReconnectRequired);
        _user.GoogleCalendarLastSyncError.Should().Be("insufficientPermissions");
        _user.GoogleAccessToken.Should().BeNull();
        _user.GoogleRefreshToken.Should().BeNull();
        levels.Should().Contain(LogLevel.Warning).And.NotContain(LogLevel.Error).And.NotContain(LogLevel.Critical);
    }

    private void StubFailure(string operation)
    {
        var error = GoogleCalendarEventFetcherTests.GoogleError(System.Net.HttpStatusCode.Forbidden,
            "insufficientPermissions", "Request had insufficient authentication scopes.");
        _api.ListCalendarsAsync("token", Arg.Any<CancellationToken>())
            .Returns(new List<CalendarListEntry> { new() { Id = "a", AccessRole = "owner" } });
        if (operation == "list")
            _api.ListCalendarsAsync("token", Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<CalendarListEntry>>>(_ => throw error);
        else if (operation == "events")
            _api.ListEventsAsync("token", "a", null, Arg.Any<CancellationToken>(), Arg.Any<CalendarReadWindow?>())
                .Returns<Task<IReadOnlyList<Event>>>(_ => throw error);
        else
        {
            _api.ListEventsAsync("token", "a", null, Arg.Any<CancellationToken>(), Arg.Any<CalendarReadWindow?>())
                .Returns(new List<Event> { new() { Id = "instance", Summary = "Recurring", RecurringEventId = "master" } });
            _api.GetEventAsync("token", "a", "master", Arg.Any<CancellationToken>())
                .Returns<Task<Event>>(_ => throw error);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
