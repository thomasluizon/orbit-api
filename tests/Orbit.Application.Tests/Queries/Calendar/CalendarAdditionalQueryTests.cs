using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using System.Linq.Expressions;

namespace Orbit.Application.Tests.Queries.Calendar;

public class GetCalendarAutoSyncStateQueryHandlerTests
{
    private readonly IGenericRepository<User> _userRepo = Substitute.For<IGenericRepository<User>>();
    private readonly GetCalendarAutoSyncStateQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetCalendarAutoSyncStateQueryHandlerTests()
    {
        _handler = new GetCalendarAutoSyncStateQueryHandler(_userRepo);
    }

    [Fact]
    public async Task Handle_UserFound_ReturnsAutoSyncState()
    {
        var user = User.Create("Test", "test@test.com").Value;
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(user);

        var result = await _handler.Handle(new GetCalendarAutoSyncStateQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Enabled.Should().BeFalse();
        result.Value.Status.Should().Be(GoogleCalendarAutoSyncStatus.Idle);
        result.Value.HasGoogleConnection.Should().BeFalse();
        result.Value.LastSyncedAt.Should().BeNull();
    }

    [Fact]
    public async Task Handle_UserNotFound_ReturnsFailure()
    {
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((User?)null);

        var result = await _handler.Handle(new GetCalendarAutoSyncStateQuery(UserId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User not found");
    }
}

public class GetCalendarSyncSuggestionsQueryHandlerTests
{
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo = Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly GetCalendarSyncSuggestionsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetCalendarSyncSuggestionsQueryHandlerTests()
    {
        _handler = new GetCalendarSyncSuggestionsQueryHandler(
            _suggestionRepo,
            Substitute.For<ILogger<GetCalendarSyncSuggestionsQueryHandler>>());
    }

    [Fact]
    public async Task Handle_NoSuggestions_ReturnsEmptyList()
    {
        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>());

        var result = await _handler.Handle(new GetCalendarSyncSuggestionsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WithSuggestions_ReturnsParsedItems()
    {
        var rawJson = """{"Id":"ev1","Title":"Team Meeting","Description":null,"StartDate":"2026-04-10","StartTime":"09:00","EndTime":"10:00","IsRecurring":false,"RecurrenceRule":null,"Reminders":[]}""";
        var suggestion = GoogleCalendarSyncSuggestion.Create(
            UserId, "ev1", "Team Meeting", DateTime.UtcNow, rawJson, DateTime.UtcNow);

        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion> { suggestion });

        var result = await _handler.Handle(new GetCalendarSyncSuggestionsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].GoogleEventId.Should().Be("ev1");
        result.Value[0].Event.Title.Should().Be("Team Meeting");
    }

    [Fact]
    public async Task Handle_InvalidJson_SkipsEntry()
    {
        var invalidSuggestion = GoogleCalendarSyncSuggestion.Create(
            UserId, "ev_bad", "Bad", DateTime.UtcNow, "not-json", DateTime.UtcNow);

        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion> { invalidSuggestion });

        var result = await _handler.Handle(new GetCalendarSyncSuggestionsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }
}
