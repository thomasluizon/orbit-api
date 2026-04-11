using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class SyncControllerTests : IDisposable
{
    private readonly OrbitDbContext _dbContext;
    private readonly SyncController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public SyncControllerTests()
    {
        var options = new DbContextOptionsBuilder<OrbitDbContext>()
            .UseInMemoryDatabase(databaseName: $"SyncControllerTests_{Guid.NewGuid()}")
            .Options;

        _dbContext = new OrbitDbContext(options);
        _dbContext.Database.EnsureCreated();

        _controller = new SyncController(_dbContext, Substitute.For<ILogger<SyncController>>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = CreateUser(UserId)
                }
            }
        };
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ClaimsPrincipal CreateUser(Guid userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task GetChanges_SinceIsTooOld_ReturnsGone()
    {
        var result = await _controller.GetChanges(DateTime.UtcNow.AddDays(-31), CancellationToken.None);

        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(StatusCodes.Status410Gone);
    }

    [Fact]
    public async Task GetChanges_ReturnsUpdatedAndDeletedEntitiesForCurrentUser()
    {
        var since = DateTime.UtcNow.AddMinutes(-5);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var activeHabit = Habit.Create(new HabitCreateParams(UserId, "Exercise", FrequencyUnit.Day, 1, DueDate: today)).Value;
        var deletedHabit = Habit.Create(new HabitCreateParams(UserId, "Stretch", FrequencyUnit.Day, 1, DueDate: today)).Value;
        deletedHabit.SoftDelete();
        var habitLog = activeHabit.Log(today, advanceDueDate: false).Value;

        var activeGoal = Goal.Create(UserId, "Read books", 12, "books").Value;
        var deletedGoal = Goal.Create(UserId, "Save money", 500, "dollars").Value;
        deletedGoal.SoftDelete();
        var goalProgressLog = GoalProgressLog.Create(activeGoal.Id, 0, 4, "Started");

        var activeTag = Tag.Create(UserId, "Health", "#00ff00").Value;
        var deletedTag = Tag.Create(UserId, "Career", "#ff0000").Value;
        deletedTag.SoftDelete();

        var notification = Notification.Create(UserId, "Reminder", "Drink water");
        var checklistTemplate = ChecklistTemplate.Create(UserId, "Morning", ["Water", "Stretch"]).Value;

        var otherUserHabit = Habit.Create(new HabitCreateParams(Guid.NewGuid(), "Other", FrequencyUnit.Day, 1, DueDate: today)).Value;
        var otherUserNotification = Notification.Create(Guid.NewGuid(), "Other Reminder", "Ignore me");

        _dbContext.AddRange(
            activeHabit,
            deletedHabit,
            habitLog,
            activeGoal,
            deletedGoal,
            goalProgressLog,
            activeTag,
            deletedTag,
            notification,
            checklistTemplate,
            otherUserHabit,
            otherUserNotification);
        await _dbContext.SaveChangesAsync();

        var result = await _controller.GetChanges(since, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<SyncController.SyncChangesResponse>().Subject;

        response.Habits.Updated.Should().HaveCount(1);
        response.Habits.Deleted.Should().ContainSingle(r => r.Id == deletedHabit.Id);
        response.HabitLogs.Updated.Should().HaveCount(1);
        response.Goals.Updated.Should().HaveCount(1);
        response.Goals.Deleted.Should().ContainSingle(r => r.Id == deletedGoal.Id);
        response.GoalProgressLogs.Updated.Should().HaveCount(1);
        response.Tags.Updated.Should().HaveCount(1);
        response.Tags.Deleted.Should().ContainSingle(r => r.Id == deletedTag.Id);
        response.Notifications.Updated.Should().ContainSingle();
        response.ChecklistTemplates.Updated.Should().ContainSingle();
        response.ServerTimestamp.Should().BeOnOrAfter(since);
    }

    [Fact]
    public async Task ProcessBatch_NoMutations_ReturnsBadRequest()
    {
        var result = await _controller.ProcessBatch(new SyncController.SyncBatchRequest([]), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ProcessBatch_TooManyMutations_ReturnsBadRequest()
    {
        var mutations = Enumerable.Range(0, 101)
            .Select(i => new SyncController.SyncMutation("habit", "delete", Guid.NewGuid(), null))
            .ToList();

        var result = await _controller.ProcessBatch(new SyncController.SyncBatchRequest(mutations), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ProcessBatch_ProcessesSupportedMutations()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var habit = Habit.Create(new HabitCreateParams(UserId, "Exercise", FrequencyUnit.Day, 1, DueDate: today)).Value;
        var goal = Goal.Create(UserId, "Read books", 12, "books").Value;
        var tag = Tag.Create(UserId, "Health", "#00ff00").Value;
        var notification = Notification.Create(UserId, "Reminder", "Drink water");

        _dbContext.AddRange(habit, goal, tag, notification);
        await _dbContext.SaveChangesAsync();

        var request = new SyncController.SyncBatchRequest(
        [
            new SyncController.SyncMutation("habit", "delete", habit.Id, null),
            new SyncController.SyncMutation("goal", "delete", goal.Id, null),
            new SyncController.SyncMutation("tag", "delete", tag.Id, null),
            new SyncController.SyncMutation("notification", "read", notification.Id, null)
        ]);

        var result = await _controller.ProcessBatch(request, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<SyncController.SyncBatchResponse>().Subject;

        response.Processed.Should().Be(4);
        response.Failed.Should().Be(0);
        response.Results.Should().OnlyContain(r => r.Status == "success");
        habit.IsDeleted.Should().BeTrue();
        goal.IsDeleted.Should().BeTrue();
        tag.IsDeleted.Should().BeTrue();
        notification.IsRead.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessBatch_InvalidMutations_ReportFailuresAndContinue()
    {
        var notification = Notification.Create(UserId, "Reminder", "Drink water");
        _dbContext.Notifications.Add(notification);
        await _dbContext.SaveChangesAsync();

        var request = new SyncController.SyncBatchRequest(
        [
            new SyncController.SyncMutation("unknown", "delete", Guid.NewGuid(), null),
            new SyncController.SyncMutation("habit", "delete", null, null),
            new SyncController.SyncMutation("goal", "update", Guid.NewGuid(), null),
            new SyncController.SyncMutation("notification", "read", notification.Id, null)
        ]);

        var result = await _controller.ProcessBatch(request, CancellationToken.None);

        var okResult = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = okResult.Value.Should().BeOfType<SyncController.SyncBatchResponse>().Subject;

        response.Processed.Should().Be(1);
        response.Failed.Should().Be(3);
        response.Results.Should().ContainInOrder(
            new SyncController.SyncMutationResult(0, "failed", "Mutation failed"),
            new SyncController.SyncMutationResult(1, "failed", "Mutation failed"),
            new SyncController.SyncMutationResult(2, "failed", "Mutation failed"),
            new SyncController.SyncMutationResult(3, "success"));
        notification.IsRead.Should().BeTrue();
    }
}
