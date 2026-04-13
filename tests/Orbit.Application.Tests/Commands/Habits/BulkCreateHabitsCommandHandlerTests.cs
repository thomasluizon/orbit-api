using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Habits;

public class BulkCreateHabitsCommandHandlerTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<GoogleCalendarSyncSuggestion> _suggestionRepo = Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly BulkCreateHabitsCommandHandler _handler;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 20);

    public BulkCreateHabitsCommandHandlerTests()
    {
        _handler = new BulkCreateHabitsCommandHandler(
            _habitRepo, _suggestionRepo, _payGate, _userDateService, _unitOfWork, _cache,
            Substitute.For<ILogger<BulkCreateHabitsCommandHandler>>());

        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _payGate.CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Today);
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Habit>().AsReadOnly());
        _suggestionRepo.FindAsync(Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new List<GoogleCalendarSyncSuggestion>().AsReadOnly());
    }

    [Fact]
    public async Task Handle_MultipleValidHabits_CreatesAllSuccessfully()
    {
        var items = new List<BulkHabitItem>
        {
            new("Read", null, FrequencyUnit.Day, 1),
            new("Exercise", "Morning workout", FrequencyUnit.Day, 1),
            new("Meditate", null, FrequencyUnit.Day, 1)
        };
        var command = new BulkCreateHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(3);
        result.Value.Results.Should().AllSatisfy(r => r.Status.Should().Be(BulkItemStatus.Success));
        await _habitRepo.Received(3).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CommitTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WithSubHabits_CreatesParentAndChildren()
    {
        var items = new List<BulkHabitItem>
        {
            new("Morning Routine", null, FrequencyUnit.Day, 1,
                SubHabits: new List<BulkHabitItem>
                {
                    new("Brush teeth", null, null, null),
                    new("Stretch", null, null, null)
                })
        };
        var command = new BulkCreateHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results.Should().HaveCount(1);
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        // Parent + 2 children = 3 AddAsync calls
        await _habitRepo.Received(3).AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
        await _payGate.Received(1).CanCreateSubHabits(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PayGateLimitReached_ReturnsPayGateFailure()
    {
        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Habit limit reached"));

        var items = new List<BulkHabitItem> { new("Habit", null, FrequencyUnit.Day, 1) };
        var command = new BulkCreateHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
        await _habitRepo.DidNotReceive().AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_SubHabitsPayGated_ReturnsPayGateFailure()
    {
        _payGate.CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure("Sub-habits are a Pro feature"));

        var items = new List<BulkHabitItem>
        {
            new("Routine", null, FrequencyUnit.Day, 1,
                SubHabits: new List<BulkHabitItem> { new("Sub", null, null, null) })
        };
        var command = new BulkCreateHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be("PAY_GATE");
    }

    [Fact]
    public async Task Handle_InvalidTitle_ReportsFailedItem()
    {
        var items = new List<BulkHabitItem>
        {
            new("Valid habit", null, FrequencyUnit.Day, 1),
            new("", null, FrequencyUnit.Day, 1) // Invalid: empty title
        };
        var command = new BulkCreateHabitsCommand(UserId, items);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Results[0].Status.Should().Be(BulkItemStatus.Success);
        result.Value.Results[1].Status.Should().Be(BulkItemStatus.Failed);
        result.Value.Results[1].Error.Should().Contain("Title");
        result.Value.Results[1].Field.Should().Be("Title");
    }

    [Fact]
    public async Task Handle_FromSyncReview_MarksMatchingSuggestionsImported()
    {
        var addedHabits = new List<Habit>();
        var suggestionEvent = new CalendarEventItem("evt_sync", "Imported Event", null, "2026-03-20", "09:00", "09:30", false, null, []);
        var suggestion = GoogleCalendarSyncSuggestion.Create(
            UserId,
            "evt_sync",
            "Imported Event",
            DateTime.SpecifyKind(new DateTime(2026, 3, 20, 0, 0, 0), DateTimeKind.Utc),
            JsonSerializer.Serialize(suggestionEvent),
            DateTime.SpecifyKind(new DateTime(2026, 3, 19, 12, 0, 0), DateTimeKind.Utc));
        var suggestions = new List<GoogleCalendarSyncSuggestion> { suggestion };

        _habitRepo.AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                addedHabits.Add(call.Arg<Habit>());
                return Task.CompletedTask;
            });
        _habitRepo.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.Arg<Expression<Func<Habit, bool>>>().Compile();
                return addedHabits.Where(predicate).ToList().AsReadOnly();
            });
        _suggestionRepo.FindAsync(Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var predicate = call.Arg<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>().Compile();
                return suggestions.Where(predicate).ToList().AsReadOnly();
            });

        var items = new List<BulkHabitItem>
        {
            new("Imported Event", null, FrequencyUnit.Day, 1,
                DueDate: Today,
                DueTime: new TimeOnly(9, 0),
                GoogleEventId: "evt_sync")
        };
        var command = new BulkCreateHabitsCommand(UserId, items, FromSyncReview: true);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        addedHabits.Should().ContainSingle();
        addedHabits[0].GoogleEventId.Should().Be("evt_sync");
        suggestion.ImportedAtUtc.Should().NotBeNull();
        suggestion.ImportedHabitId.Should().Be(addedHabits[0].Id);
    }

    [Fact]
    public async Task Handle_InvalidatesSummaryCache()
    {
        var realToday = DateOnly.FromDateTime(DateTime.UtcNow);
        var cacheKey = $"summary:{UserId}:{realToday:yyyy-MM-dd}:en";
        _cache.Set(cacheKey, "cached-summary");

        var items = new List<BulkHabitItem> { new("Habit", null, FrequencyUnit.Day, 1) };
        var command = new BulkCreateHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        _cache.TryGetValue(cacheKey, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Handle_UsesTransaction()
    {
        var items = new List<BulkHabitItem> { new("Habit", null, FrequencyUnit.Day, 1) };
        var command = new BulkCreateHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        await _unitOfWork.Received(1).BeginTransactionAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CommitTransactionAsync(Arg.Any<CancellationToken>());
        await _unitOfWork.DidNotReceive().RollbackTransactionAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_NoSubHabits_DoesNotCheckSubHabitGate()
    {
        var items = new List<BulkHabitItem> { new("Habit", null, FrequencyUnit.Day, 1) };
        var command = new BulkCreateHabitsCommand(UserId, items);

        await _handler.Handle(command, CancellationToken.None);

        await _payGate.DidNotReceive().CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
