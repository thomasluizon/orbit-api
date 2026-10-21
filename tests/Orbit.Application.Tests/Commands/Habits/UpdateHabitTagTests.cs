using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using NSubstitute.Extensions;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Application.Tests.Commands.Habits;

public sealed class UpdateHabitTagTests : IDisposable
{
    private readonly Guid _userId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);
    private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<Tag> _tags = Substitute.For<IGenericRepository<Tag>>();
    private readonly IGenericRepository<Goal> _goals = Substitute.For<IGenericRepository<Goal>>();
    private readonly IGenericRepository<SentReminder> _reminders = Substitute.For<IGenericRepository<SentReminder>>();
    private readonly IAppConfigService _config = Substitute.For<IAppConfigService>();
    private readonly IGoalCompletionService _completion = Substitute.For<IGoalCompletionService>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly Habit _habit;
    private readonly Tag _oldTag;
    private readonly Goal _oldGoal;
    private readonly List<Tag> _availableTags = [];
    private readonly List<Goal> _availableGoals = [];
    private readonly UpdateHabitCommandHandler _handler;
    private bool _insideTransaction;
    private bool _advisoryLocked;
    private bool _userLocked;

    public UpdateHabitTagTests()
    {
        _habit = Habit.Create(new HabitCreateParams(_userId, "Original", FrequencyUnit.Day, 1, Today,
            DueTime: new TimeOnly(9, 0), ReminderEnabled: true, ReminderTimes: [15],
            ChecklistItems: [new("Duplicate", false), new("Duplicate", true), new("Other", false)])).Value;
        _oldTag = Tag.Create(_userId, "Old", "#ffffff").Value;
        _oldGoal = Goal.Create(_userId, "Old goal", 10, "items").Value;
        _habit.AddTag(_oldTag);
        _habit.AddGoal(_oldGoal);
        _availableGoals.Add(_oldGoal);
        _config.GetAsync(AppConfigKeys.MaxTagsPerHabit, AppConstants.MaxTagsPerHabit, Arg.Any<CancellationToken>())
            .Returns(AppConstants.MaxTagsPerHabit);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                _insideTransaction.Should().BeFalse();
                _insideTransaction = true;
                try { return await call.Arg<Func<CancellationToken, Task<Result>>>()(call.Arg<CancellationToken>()); }
                finally { _insideTransaction = false; }
            });
        _unitOfWork.AcquireAdvisoryLockAsync(HabitCeilingLock.ForUser(_userId), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _insideTransaction.Should().BeTrue();
                _advisoryLocked = true;
                return Task.CompletedTask;
            });
        _unitOfWork.AcquireUserRowLockAsync(_userId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _advisoryLocked.Should().BeTrue();
            _userLocked = true;
            return Task.CompletedTask;
        });
        _habits.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                AssertOwnedTransaction();
                return call.Arg<Expression<Func<Habit, bool>>>().Compile()(_habit) ? _habit : null;
            });
        _tags.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                AssertOwnedTransaction();
                return _availableTags.Where(call.Arg<Expression<Func<Tag, bool>>>().Compile()).ToList();
            });
        _goals.FindTrackedAsync(Arg.Any<Expression<Func<Goal, bool>>>(),
                Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                AssertOwnedTransaction();
                return _availableGoals.Where(call.Arg<Expression<Func<Goal, bool>>>().Compile()).ToList();
            });
        _unitOfWork.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            AssertOwnedTransaction();
            return 1;
        });
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Today);
        _handler = new(_habits, _reminders, _goals, Substitute.For<IPayGateService>(), _completion,
            dates, _unitOfWork, _cache, _tags, _config);
    }

    [Fact]
    public async Task OmittedOrNullTags_PreserveAssignmentsAndSkipTagLookup()
    {
        (await _handler.Handle(Command(), CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await _handler.Handle(Command() with { TagIds = null }, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _habit.Tags.Should().Equal(_oldTag);
        _habit.Goals.Should().Equal(_oldGoal);
        await _tags.DidNotReceive().FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());
        await _config.DidNotReceive().GetAsync(AppConfigKeys.MaxTagsPerHabit, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EmptyTags_ClearAssignmentsAndPreserveGoals()
    {
        (await _handler.Handle(Command() with { TagIds = [] }, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _habit.Tags.Should().BeEmpty();
        _habit.Goals.Should().Equal(_oldGoal);
        await _unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CombinedUpdate_PreservesChecklistOrderAndCheckedValuesAndReplaysAsASet()
    {
        var first = AddTag();
        var second = AddTag();
        var goal = Goal.Create(_userId, "New goal", 10, "items").Value;
        _availableGoals.Add(goal);
        ChecklistItem[] checklist = [new("Duplicate", true), new("Other", false), new("Duplicate", false)];
        var command = Command() with
        {
            Description = "Changed",
            DueDate = Today.AddDays(2),
            Emoji = "book",
            GoalIds = [goal.Id],
            TagIds = [second.Id, first.Id, second.Id],
            Options = new(DueTime: new TimeOnly(10, 0), ReminderEnabled: false,
                ReminderTimes: [30], ChecklistItems: checklist, EndDate: Today.AddDays(5))
        };
        var syncCount = 0;
        _completion.SyncDerivedGoalsAsync(_userId, Arg.Any<IReadOnlyCollection<Guid>>(), Today, false, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                AssertOwnedTransaction();
                call.Arg<IReadOnlyCollection<Guid>>().Should().BeEquivalentTo(
                    syncCount == 0 ? [_oldGoal.Id, goal.Id] : new[] { goal.Id });
                _habit.Tags.Should().BeEquivalentTo([first, second]);
                _habit.Goals.Should().Equal(goal);
                _habit.ChecklistItems.Should().Equal(checklist);
                syncCount++;
                return Task.FromResult<IReadOnlyList<GoalCompletionUpdate>>([]);
            });

        (await _handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var afterFirst = Snapshot();
        (await _handler.Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue();

        Snapshot().Should().Be(afterFirst);
        _habit.Title.Should().Be("Changed");
        _habit.Description.Should().Be("Changed");
        _habit.DueDate.Should().Be(Today.AddDays(2));
        _habit.DueTime.Should().Be(new TimeOnly(10, 0));
        _habit.EndDate.Should().Be(Today.AddDays(5));
        _habit.ReminderEnabled.Should().BeFalse();
        _habit.ReminderTimes.Should().Equal(30);
        syncCount.Should().Be(2);
        await _tags.Received(2).FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("deleted")]
    [InlineData("empty-id")]
    public async Task UnresolvedTags_RejectBeforeAnyHabitOrAssociationMutation(string kind)
    {
        var valid = AddTag();
        var invalid = Tag.Create(kind == "foreign" ? Guid.NewGuid() : _userId, "Invalid", "#ffffff").Value;
        if (kind == "deleted") invalid.SoftDelete();
        if (kind is "foreign" or "deleted") _availableTags.Add(invalid);
        var before = Snapshot();
        var id = kind == "empty-id" ? Guid.Empty : invalid.Id;

        var result = await _handler.Handle(Command() with { TagIds = [valid.Id, id], GoalIds = [] }, CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.TagNotFound);
        Snapshot().Should().Be(before);
        await AssertNoSave();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("deleted")]
    public async Task UnresolvedGoals_RejectBeforeTagChecklistAndReminderMutation(string kind)
    {
        var valid = AddTag();
        var invalid = Goal.Create(kind == "foreign" ? Guid.NewGuid() : _userId, "Invalid goal", 10, "items").Value;
        if (kind == "deleted") invalid.SoftDelete();
        if (kind != "missing") _availableGoals.Add(invalid);
        var before = Snapshot();
        var result = await _handler.Handle(Command() with
        {
            TagIds = [valid.Id],
            GoalIds = [invalid.Id],
            Options = new(DueTime: new TimeOnly(10, 0), ChecklistItems: [])
        }, CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.GoalNotFound);
        Snapshot().Should().Be(before);
        await _reminders.DidNotReceive().FindAsync(Arg.Any<Expression<Func<SentReminder, bool>>>(), Arg.Any<CancellationToken>());
        await AssertNoSave();
    }

    [Fact]
    public async Task ConfiguredCap_CountsSubmittedDuplicatesBeforeResolution()
    {
        var tag = AddTag();
        _config.GetAsync(AppConfigKeys.MaxTagsPerHabit, AppConstants.MaxTagsPerHabit, Arg.Any<CancellationToken>()).Returns(1);
        var before = Snapshot();
        var result = await _handler.Handle(Command() with { TagIds = [tag.Id, tag.Id] }, CancellationToken.None);
        result.ErrorCode.Should().Be(ErrorCodes.MaxTagsPerHabit);
        Snapshot().Should().Be(before);
        await AssertNoSave();
        (await _handler.Handle(Command() with { TagIds = [tag.Id] }, CancellationToken.None)).IsSuccess.Should().BeTrue();
        _habit.Tags.Should().Equal(tag);
    }

    [Fact]
    public async Task InvalidCoreField_DoesNotChangeResolvedAssociations()
    {
        var before = Snapshot();
        var result = await _handler.Handle(Command() with { Title = "", TagIds = [], GoalIds = [] }, CancellationToken.None);
        result.IsFailure.Should().BeTrue();
        Snapshot().Should().Be(before);
        await AssertNoSave();
    }

    [Fact]
    public async Task CancellationDuringResolution_IsObservedBeforeMutation()
    {
        using var cancellation = new CancellationTokenSource();
        _tags.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), cancellation.Token).Returns(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult<IReadOnlyList<Tag>>([]);
        });
        var before = Snapshot();
        var act = () => _handler.Handle(Command() with { TagIds = [] }, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        Snapshot().Should().Be(before);
        await AssertNoSave();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveFailureOrCancellation_PropagatesThroughOwnedTransactionWithoutInvalidatingCache(bool cancel)
    {
        var key = $"goal-review:{_userId}:en";
        _cache.Set(key, "cached");
        using var cancellation = new CancellationTokenSource();
        Exception failure = cancel ? new OperationCanceledException(cancellation.Token) : new InvalidOperationException("Storage failed");
        _unitOfWork.Configure().SaveChangesAsync(cancellation.Token).Returns<int>(_ =>
        {
            AssertOwnedTransaction();
            _habit.Tags.Should().BeEmpty();
            throw failure;
        });
        var act = () => _handler.Handle(Command() with { TagIds = [] }, cancellation.Token);
        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
        _insideTransaction.Should().BeFalse();
        _cache.TryGetValue(key, out _).Should().BeTrue();
    }

    private UpdateHabitCommand Command() => new(_userId, _habit.Id, "Changed", null, FrequencyUnit.Day, 1);
    private Tag AddTag()
    {
        var tag = Tag.Create(_userId, "Selection", "#ffffff").Value;
        _availableTags.Add(tag);
        return tag;
    }
    private string Snapshot() => JsonSerializer.Serialize(new
    {
        _habit.Title,
        _habit.Description,
        _habit.DueDate,
        _habit.DueTime,
        _habit.EndDate,
        _habit.ReminderEnabled,
        _habit.ReminderTimes,
        _habit.ChecklistItems,
        Tags = _habit.Tags.Select(t => t.Id),
        Goals = _habit.Goals.Select(g => g.Id)
    });
    private void AssertOwnedTransaction()
    {
        _insideTransaction.Should().BeTrue();
        _advisoryLocked.Should().BeTrue();
        _userLocked.Should().BeTrue();
    }
    private async Task AssertNoSave()
    {
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _completion.DidNotReceive().SyncDerivedGoalsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Any<DateOnly>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
    public void Dispose() => _cache.Dispose();
}
