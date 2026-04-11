using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Calendar;

public class GetCalendarSyncSuggestionsQueryHandlerTests
{
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo =
        Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly ILogger<GetCalendarSyncSuggestionsQueryHandler> _logger =
        Substitute.For<ILogger<GetCalendarSyncSuggestionsQueryHandler>>();
    private readonly GetCalendarSyncSuggestionsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();

    public GetCalendarSyncSuggestionsQueryHandlerTests()
    {
        _handler = new GetCalendarSyncSuggestionsQueryHandler(_suggestionRepo, _logger);
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
    public async Task Handle_WithValidSuggestions_ReturnsDeserializedItems()
    {
        var eventItem = new CalendarEventItem("event-1", "Morning Yoga", null, "2025-01-15", "09:00", "10:00", false, null, []);
        var rawJson = JsonSerializer.Serialize(eventItem);
        var now = DateTime.UtcNow;

        var suggestion = GoogleCalendarSyncSuggestion.Create(
            UserId, "gcal-1", "Morning Yoga", now, rawJson, now);

        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion> { suggestion });

        var result = await _handler.Handle(new GetCalendarSyncSuggestionsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].GoogleEventId.Should().Be("gcal-1");
        result.Value[0].Event.Title.Should().Be("Morning Yoga");
    }

    [Fact]
    public async Task Handle_InvalidJson_SkipsBadSuggestionAndContinues()
    {
        var now = DateTime.UtcNow;

        var badSuggestion = GoogleCalendarSyncSuggestion.Create(
            UserId, "gcal-bad", "Bad Event", now, "not-valid-json", now);

        var goodEventItem = new CalendarEventItem("event-2", "Good Event", null, "2025-01-15", "09:00", "10:00", false, null, []);
        var goodJson = JsonSerializer.Serialize(goodEventItem);
        var goodSuggestion = GoogleCalendarSyncSuggestion.Create(
            UserId, "gcal-good", "Good Event", now.AddHours(1), goodJson, now);

        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion> { badSuggestion, goodSuggestion });

        var result = await _handler.Handle(new GetCalendarSyncSuggestionsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(1);
        result.Value[0].GoogleEventId.Should().Be("gcal-good");
    }

    [Fact]
    public async Task Handle_MultipleSuggestions_OrderedByStartDate()
    {
        var now = DateTime.UtcNow;
        var items = new List<GoogleCalendarSyncSuggestion>();

        for (int i = 2; i >= 0; i--)
        {
            var eventItem = new CalendarEventItem($"evt-{i}", $"Event {i}", null, "2025-01-15", null, null, false, null, []);
            var json = JsonSerializer.Serialize(eventItem);
            items.Add(GoogleCalendarSyncSuggestion.Create(
                UserId, $"gcal-{i}", $"Event {i}", now.AddHours(i), json, now));
        }

        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(items);

        var result = await _handler.Handle(new GetCalendarSyncSuggestionsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(3);
        result.Value[0].GoogleEventId.Should().Be("gcal-0");
        result.Value[1].GoogleEventId.Should().Be("gcal-1");
        result.Value[2].GoogleEventId.Should().Be("gcal-2");
    }
}
