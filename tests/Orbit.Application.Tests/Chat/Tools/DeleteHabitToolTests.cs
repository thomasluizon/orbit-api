using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;

namespace Orbit.Application.Tests.Chat.Tools;

public class DeleteHabitToolTests
{
    private readonly IGenericRepository<Habit> _habitRepo = Substitute.For<IGenericRepository<Habit>>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly DeleteHabitTool _tool;

    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 4, 3);

    public DeleteHabitToolTests()
    {
        _tool = new DeleteHabitTool(_mediator, _habitRepo);
        _mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
    }

    [Fact]
    public async Task SuccessfulDelete_ReturnsSuccessAndSoftDeletesHabit()
    {
        var habit = CreateHabit("Water");
        SetupHabitFound(habit);

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeTrue();
        result.EntityName.Should().Be("Water");
        result.EntityId.Should().Be(habit.Id.ToString());
        await _mediator.Received(1).Send(
            Arg.Is<DeleteHabitCommand>(command => command.UserId == UserId && command.HabitId == habit.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HabitNotFound_ReturnsError()
    {
        var id = Guid.NewGuid();
        SetupHabitNotFound();

        var result = await Execute($$$"""{"habit_id": "{{{id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("not found");
        await _mediator.DidNotReceive().Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CommandFailure_ReturnsError()
    {
        var habit = CreateHabit("Water");
        SetupHabitFound(habit);
        _mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit not found."));

        var result = await Execute($$$"""{"habit_id": "{{{habit.Id}}}"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Be("Habit not found.");
    }

    [Fact]
    public async Task MissingHabitId_ReturnsError()
    {
        var result = await Execute("{}");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task InvalidGuid_ReturnsError()
    {
        var result = await Execute("""{"habit_id": "not-a-guid"}""");

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("habit_id is required");
    }

    [Fact]
    public async Task WrongUser_CannotDeleteAnothersHabit_OwnerCan_RealContext()
    {
        var databaseName = $"DeleteHabitIsolation_{Guid.NewGuid()}";
        Guid habitId;
        Guid childId;
        Guid httpHabitId;
        Guid httpChildId;
        await using (var seed = CreateContext(databaseName))
        {
            var ownerHabit = CreateHabit("Owner-only habit");
            var child = Habit.Create(new HabitCreateParams(UserId, "Child", FrequencyUnit.Day, 1,
                DueDate: Today, ParentHabitId: ownerHabit.Id)).Value;
            var httpHabit = CreateHabit("HTTP habit");
            var httpChild = Habit.Create(new HabitCreateParams(UserId, "HTTP child", FrequencyUnit.Day, 1,
                DueDate: Today, ParentHabitId: httpHabit.Id)).Value;
            seed.Habits.AddRange(ownerHabit, child, httpHabit, httpChild);
            await seed.SaveChangesAsync();
            habitId = ownerHabit.Id;
            childId = child.Id;
            httpHabitId = httpHabit.Id;
            httpChildId = httpChild.Id;
        }

        await using var context = CreateContext(databaseName);
        var repo = new GenericRepository<Habit>(context);
        var work = new UnitOfWork(context, new DatabaseConnectionSettings());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(Today);
        var handler = new DeleteHabitCommandHandler(repo, Substitute.For<IUserStreakService>(), work,
            dates, new MemoryCache(new MemoryCacheOptions()));
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<DeleteHabitCommand>(), call.Arg<CancellationToken>()));
        var tool = new DeleteHabitTool(mediator, repo);

        var attackerId = Guid.NewGuid();
        var attackerResult = await tool.ExecuteAsync(ArgsFor(habitId), attackerId, CancellationToken.None);
        await context.SaveChangesAsync();

        attackerResult.Success.Should().BeFalse();
        attackerResult.Error.Should().Contain("not found");
        await using (var afterAttack = CreateContext(databaseName))
            (await afterAttack.Habits.AnyAsync(h => h.Id == habitId))
                .Should().BeTrue("a foreign user must not delete another user's habit");

        var ownerResult = await tool.ExecuteAsync(ArgsFor(habitId), UserId, CancellationToken.None);
        var httpResult = await handler.Handle(new DeleteHabitCommand(UserId, httpHabitId), CancellationToken.None);

        ownerResult.Success.Should().BeTrue("the owner can delete their own habit");
        httpResult.IsSuccess.Should().BeTrue();
        await using (var afterOwner = CreateContext(databaseName))
        {
            var deleted = await afterOwner.Habits.IgnoreQueryFilters()
                .Where(h => h.Id == habitId || h.Id == childId).ToListAsync();
            var httpDeleted = await afterOwner.Habits.IgnoreQueryFilters()
                .Where(h => h.Id == httpHabitId || h.Id == httpChildId).ToListAsync();
            deleted.Should().HaveCount(2);
            deleted.Should().OnlyContain(h => h.IsDeleted && h.DeletedAtUtc != null);
            deleted.Select(h => h.DeletedAtUtc).Distinct().Should().ContainSingle();
            httpDeleted.Should().HaveCount(2);
            httpDeleted.Should().OnlyContain(h => h.IsDeleted && h.DeletedAtUtc != null);
            httpDeleted.Select(h => h.DeletedAtUtc).Distinct().Should().ContainSingle();
            (await afterOwner.Habits.AnyAsync(h => h.Id == habitId || h.Id == childId)).Should().BeFalse();
            (await afterOwner.Habits.AnyAsync(h => h.Id == httpHabitId || h.Id == httpChildId)).Should().BeFalse();
        }
    }

    private static JsonElement ArgsFor(Guid habitId) =>
        JsonDocument.Parse($$"""{"habit_id":"{{habitId}}"}""").RootElement;

    private static OrbitDbContext CreateContext(string databaseName) =>
        new(new DbContextOptionsBuilder<OrbitDbContext>().UseInMemoryDatabase(databaseName).Options);

    private static Habit CreateHabit(string title)
    {
        return Habit.Create(new HabitCreateParams(UserId, title, FrequencyUnit.Day, 1, DueDate: Today)).Value;
    }

    private void SetupHabitFound(Habit habit)
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns(habit);
    }

    private void SetupHabitNotFound()
    {
        _habitRepo.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()
        ).Returns((Habit?)null);
    }

    private async Task<ToolResult> Execute(string json)
    {
        var args = JsonDocument.Parse(json).RootElement;
        return await _tool.ExecuteAsync(args, UserId, CancellationToken.None);
    }
}
