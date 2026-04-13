using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Queries.Calendar;

public class GetCalendarSyncSuggestionsQueryHandlerTests
{
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo =
        Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly IGenericRepository<Habit> _habitRepo =
        Substitute.For<IGenericRepository<Habit>>();
    private readonly IUserDateService _userDateService =
        Substitute.For<IUserDateService>();
    private readonly ILogger<GetCalendarSyncSuggestionsQueryHandler> _logger =
        Substitute.For<ILogger<GetCalendarSyncSuggestionsQueryHandler>>();
    private readonly GetCalendarSyncSuggestionsQueryHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 10);

    public GetCalendarSyncSuggestionsQueryHandlerTests()
    {
        _handler = new GetCalendarSyncSuggestionsQueryHandler(
            _suggestionRepo,
            _habitRepo,
            _userDateService,
            _logger);
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
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
        var eventItem = new CalendarEventItem("event-1", "Morning Yoga", null, "2026-04-15", "09:00", "10:00", false, null, []);
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

        var goodEventItem = new CalendarEventItem("event-2", "Good Event", null, "2026-04-15", "09:00", "10:00", false, null, []);
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
            var eventItem = new CalendarEventItem($"evt-{i}", $"Event {i}", null, $"2026-04-1{i + 1}", null, null, false, null, []);
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

    [Fact]
    public async Task Handle_PastSuggestions_AreExcluded()
    {
        var pastSuggestion = CreateSuggestion("gcal-past", "Past Event", "2026-04-08", "09:00");
        var futureSuggestion = CreateSuggestion("gcal-future", "Future Event", "2026-04-12", "09:00");

        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion> { pastSuggestion, futureSuggestion }.AsReadOnly());

        var result = await _handler.Handle(new GetCalendarSyncSuggestionsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle();
        result.Value[0].GoogleEventId.Should().Be("gcal-future");
    }

    [Fact]
    public async Task Handle_ExistingHabitGoogleEventId_ExcludesMatchingSuggestion()
    {
        var suggestion = CreateSuggestion("gcal-1", "Morning Yoga", "2026-04-15", "09:00");
        var matchingHabit = CreateHabit("Morning Yoga", new DateOnly(2026, 4, 15), new TimeOnly(9, 0), "gcal-1");

        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion> { suggestion }.AsReadOnly());
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { matchingHabit }.AsReadOnly());

        var result = await _handler.Handle(new GetCalendarSyncSuggestionsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_LegacyMatchedHabit_ExcludesMatchingSuggestion()
    {
        var suggestion = CreateSuggestion("gcal-legacy", "Morning Yoga", "2026-04-15", "09:00");
        var legacyHabit = CreateHabit("Morning Yoga", new DateOnly(2026, 4, 15), new TimeOnly(9, 0));

        _suggestionRepo.FindAsync(
            Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion> { suggestion }.AsReadOnly());
        _habitRepo.FindAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<CancellationToken>())
            .Returns(new List<Habit> { legacyHabit }.AsReadOnly());

        var result = await _handler.Handle(new GetCalendarSyncSuggestionsQuery(UserId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEmpty();
    }

    private static GoogleCalendarSyncSuggestion CreateSuggestion(
        string googleEventId,
        string title,
        string startDate,
        string? startTime)
    {
        var eventItem = new CalendarEventItem(googleEventId, title, null, startDate, startTime, null, false, null, []);
        var rawJson = JsonSerializer.Serialize(eventItem);
        var discoveredAt = DateTime.SpecifyKind(new DateTime(2026, 4, 10, 12, 0, 0), DateTimeKind.Utc);
        var startDateUtc = DateTime.SpecifyKind(DateOnly.Parse(startDate).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

        return GoogleCalendarSyncSuggestion.Create(
            UserId,
            googleEventId,
            title,
            startDateUtc,
            rawJson,
            discoveredAt);
    }

    private static Habit CreateHabit(
        string title,
        DateOnly dueDate,
        TimeOnly? dueTime,
        string? googleEventId = null)
    {
        return Habit.Create(new HabitCreateParams(
            UserId,
            title,
            FrequencyUnit.Day,
            1,
            DueDate: dueDate,
            DueTime: dueTime,
            GoogleEventId: googleEventId)).Value;
    }
}
