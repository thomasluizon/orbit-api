using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tags.Commands;
using Orbit.Application.Tags.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class ReusedHabitPreviewTests : IDisposable
{
    private static readonly string[] NewTagNames = ["New tag"];

    private readonly SqliteOrbitDbContextFactory _factory = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly PendingOperationChangePreviewer _previewer;
    private readonly BulkCreateHabitsTool _tool;
    private readonly Guid _userId;
    private readonly Guid _habitId;
    private readonly Guid _parentId;
    private readonly Guid _newParentId;
    private readonly Guid _tagId;
    private readonly Guid _newTagId;
    private readonly Guid _goalId;
    private readonly Guid _newGoalId;

    public ReusedHabitPreviewTests()
    {
        var db = _factory.Context;
        var user = User.Create("Alex", "alex@example.com").Value;
        _userId = user.Id;
        var parent = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Morning", FrequencyUnit.Day, 1, Today)).Value;
        var destination = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Evening", FrequencyUnit.Day, 1, Today)).Value;
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring, _userId, "Run", FrequencyUnit.Day, 1, Today, ParentHabitId: parent.Id)).Value;
        var tag = Tag.Create(_userId, "Old tag", "#123456").Value;
        var newTag = Tag.Create(_userId, "New tag", "#123456").Value;
        var goal = Goal.Create(_userId, "Old goal", 10, "runs").Value;
        var newGoal = Goal.Create(_userId, "New goal", 20, "runs").Value;
        habit.AddTag(tag);
        habit.AddGoal(goal);
        db.Users.Add(user);
        db.Habits.AddRange(parent, destination, habit);
        db.Tags.AddRange(tag, newTag);
        db.Goals.AddRange(goal, newGoal);
        db.SaveChanges();
        (_habitId, _parentId, _newParentId, _tagId, _newTagId, _goalId, _newGoalId) =
            (habit.Id, parent.Id, destination.Id, tag.Id, newTag.Id, goal.Id, newGoal.Id);
        db.ChangeTracker.Clear();

        var habits = new GenericRepository<Habit>(db);
        var goals = new GenericRepository<Goal>(db);
        var tags = new GenericRepository<Tag>(db);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Today);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
        var gate = Substitute.For<IPayGateService>();
        gate.CanCreateHabits(_userId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var update = new UpdateHabitCommandHandler(habits, new GenericRepository<SentReminder>(db), goals,
            gate, new GoalCompletionService(goals, Substitute.For<IGamificationService>(), work, dates),
            dates, work, _cache, tags, config);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => update.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
        var getTags = new GetTagsQueryHandler(tags, _cache);
        var assignTags = new AssignTagsCommandHandler(habits, tags, config, work);
        mediator.Send(Arg.Any<GetTagsQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => getTags.Handle(call.Arg<GetTagsQuery>(), call.Arg<CancellationToken>()));
        mediator.Send(Arg.Any<AssignTagsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => assignTags.Handle(call.Arg<AssignTagsCommand>(), call.Arg<CancellationToken>()));
        var create = new CreateHabitCommandHandler(new(habits, tags, goals), dates, gate,
            Substitute.For<IGamificationService>(), Substitute.For<IGoalCompletionService>(), work, _cache,
            NullLogger<CreateHabitCommandHandler>.Instance);
        var bulk = new BulkCreateHabitsCommandHandler(new(habits, new GenericRepository<GoogleCalendarSyncSuggestion>(db), tags),
            gate, dates, work, _cache, config);
        mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => create.Handle(call.Arg<CreateHabitCommand>(), call.Arg<CancellationToken>()));
        mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => bulk.Handle(call.Arg<BulkCreateHabitsCommand>(), call.Arg<CancellationToken>()));
        _tool = new BulkCreateHabitsTool(mediator);
        _previewer = new(habits, goals, tags, dates, new AiToolRegistry([_tool]));
    }

    private static DateOnly Today => new(2026, 4, 3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewAndExecution_ExplicitAssociationReplacementOrRemovalAgree(bool clear)
    {
        var arguments = Arguments(new()
        {
            ["tag_ids"] = clear ? Array.Empty<Guid>() : new[] { _newTagId },
            ["goal_ids"] = clear ? Array.Empty<Guid>() : new[] { _newGoalId },
            ["parent_habit_id"] = _newParentId
        });
        var preview = await Preview(arguments);
        var fields = preview.Items!.Single().Fields;
        fields.Single(field => field.Field == "tag_ids").OldValue.Should().Be("Old tag");
        fields.Single(field => field.Field == "goal_ids").OldValue.Should().Be("Old goal");
        fields.Single(field => field.Field == "parent_habit_id").OldValue.Should().Be("Morning");
        fields.Single(field => field.Field == "parent_habit_id").NewValue.Should().Be("Evening");
        fields.Single(field => field.Field == "tag_ids").NewValue.Should().Be(clear ? "" : "New tag");
        fields.Single(field => field.Field == "goal_ids").NewValue.Should().Be(clear ? "" : "New goal");
        fields.Should().OnlyContain(field => field.EntityId == _habitId);

        (await _tool.ExecuteAsync(arguments, _userId, default)).Success.Should().BeTrue();

        using var verify = _factory.CreateContext();
        var saved = verify.Habits.Include(habit => habit.Tags).Include(habit => habit.Goals).Single(habit => habit.Id == _habitId);
        saved.Tags.Select(tag => tag.Id).Should().BeEquivalentTo(clear ? [] : new[] { _newTagId });
        saved.Goals.Select(goal => goal.Id).Should().BeEquivalentTo(clear ? [] : new[] { _newGoalId });
        saved.ParentHabitId.Should().Be(_newParentId);
    }

    [Fact]
    public async Task PreviewAndExecution_OmittedAssociationsPreserveEveryLinkAndParent()
    {
        var arguments = Arguments(new() { ["description"] = "Outside" });
        var preview = await Preview(arguments);
        var parent = preview.Items!.Single().Fields.Single(field => field.Field == "parent_habit_id");
        parent.OldValue.Should().Be("Morning");
        parent.NewValue.Should().Be("Morning");
        parent.IsEditable.Should().BeFalse();

        (await _tool.ExecuteAsync(arguments, _userId, default)).Success.Should().BeTrue();

        using var verify = _factory.CreateContext();
        var saved = verify.Habits.Include(habit => habit.Tags).Include(habit => habit.Goals).Single(habit => habit.Id == _habitId);
        saved.Tags.Should().ContainSingle().Which.Id.Should().Be(_tagId);
        saved.Goals.Should().ContainSingle().Which.Id.Should().Be(_goalId);
        saved.ParentHabitId.Should().Be(_parentId);
        saved.Description.Should().Be("Outside");
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("goal")]
    [InlineData("parent")]
    public async Task Preview_AssociationChangesInvalidateApprovalEvenWhenUpdatedAtIsUnchanged(string association)
    {
        var arguments = Arguments(new() { ["tag_ids"] = new[] { _newTagId }, ["goal_ids"] = new[] { _newGoalId } });
        var before = await Preview(arguments);
        var db = _factory.Context;
        var habit = db.Habits.Include(value => value.Tags).Include(value => value.Goals).Single(value => value.Id == _habitId);
        var updatedAtUtc = habit.UpdatedAtUtc;
        if (association == "tag") habit.RemoveTag(habit.Tags.Single());
        if (association == "goal") habit.RemoveGoal(habit.Goals.Single());
        if (association == "parent") habit.SetParentHabitId(_newParentId);
        typeof(Habit).GetProperty(nameof(Habit.UpdatedAtUtc))!.SetValue(habit, updatedAtUtc);
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var after = await Preview(arguments);

        after.PreviewFingerprint.Should().NotBe(before.PreviewFingerprint);
        after.Items![0].StateFingerprint.Should().NotBe(before.Items![0].StateFingerprint);
    }

    [Fact]
    public async Task Preview_NestedReuseShowsPreviousLinksAndBindsTheirState()
    {
        var child = new Dictionary<string, object?> { ["habit_id"] = _habitId, ["title"] = "Run", ["tag_ids"] = new[] { _newTagId } };
        var arguments = JsonSerializer.SerializeToElement(new { habits = new[] { new { title = "Routine", frequency_unit = "Day", sub_habits = new[] { child } } } });

        var preview = await Preview(arguments);

        preview.Items![0].Fields.Single(field => field.Field == "sub_habits").OldValue.Should().Contain("Old tag").And.Contain("Morning");
        var db = _factory.Context;
        var habit = db.Habits.Include(value => value.Tags).Single(value => value.Id == _habitId);
        habit.RemoveTag(habit.Tags.Single());
        db.SaveChanges();
        db.ChangeTracker.Clear();
        (await Preview(arguments)).PreviewFingerprint.Should().NotBe(preview.PreviewFingerprint);

        (await _tool.ExecuteAsync(arguments, _userId, default)).Success.Should().BeTrue();
        using var verify = _factory.CreateContext();
        verify.Habits.Should().HaveCount(4);
        var routine = verify.Habits.Single(value => value.Title == "Routine");
        var reused = verify.Habits.Include(value => value.Tags).Single(value => value.Id == _habitId);
        reused.ParentHabitId.Should().Be(routine.Id);
        reused.Tags.Should().ContainSingle().Which.Id.Should().Be(_newTagId);
    }

    [Theory]
    [InlineData("tag_ids")]
    [InlineData("goal_ids")]
    public async Task Preview_UnownedAssociationCannotProduceAnApprovableCard(string field)
    {
        var arguments = Arguments(new() { [field] = new[] { Guid.NewGuid() } });

        (await _previewer.PreviewAsync(_userId, _tool.Name, arguments)).Value.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewAndExecution_TagNamesShowAndApplyReplacement(bool clear)
    {
        var arguments = Arguments(new() { ["tag_names"] = clear ? Array.Empty<string>() : NewTagNames });
        var preview = await Preview(arguments);
        preview.Items![0].Fields.Single(field => field.Field == "tag_names").OldValue.Should().Be("Old tag");

        (await _tool.ExecuteAsync(arguments, _userId, default)).Success.Should().BeTrue();

        using var verify = _factory.CreateContext();
        var saved = verify.Habits.Include(habit => habit.Tags).Include(habit => habit.Goals).Single(habit => habit.Id == _habitId);
        saved.Tags.Select(tag => tag.Id).Should().BeEquivalentTo(clear ? [] : new[] { _newTagId });
        saved.Goals.Should().ContainSingle().Which.Id.Should().Be(_goalId);
        saved.ParentHabitId.Should().Be(_parentId);
    }

    private JsonElement Arguments(Dictionary<string, object?> edits)
    {
        edits["habit_id"] = _habitId;
        edits["title"] = "Run";
        return JsonSerializer.SerializeToElement(new { habits = new[] { edits } });
    }

    private async Task<PendingOperationChangePreview> Preview(JsonElement arguments)
    {
        var preview = await _previewer.PreviewAsync(_userId, _tool.Name, arguments);
        preview.IsSuccess.Should().BeTrue();
        preview.Value.Should().NotBeNull();
        return preview.Value!;
    }

    public void Dispose()
    {
        _cache.Dispose();
        _factory.Dispose();
    }
}
