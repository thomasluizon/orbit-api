using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Orbit.Application.Calendar.Commands;
using Orbit.Infrastructure.Configuration;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Calendar.Queries;
using Orbit.Application.Calendar.Services;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Chat;
using Orbit.Application.Common;
using Orbit.Application.Goals.Queries;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services.Prompts;
using Orbit.Infrastructure.Services.Prompts.Sections.Dynamic;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Mcp;

public sealed class FieldReadbackTests
{
    private static readonly DateOnly Today = new(2026, 10, 19);
    private static ClaimsPrincipal Principal(Guid id) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString())], "Test"));
    private static McpExecutorBridge Bridge() => new(Substitute.For<IAgentOperationExecutor>());

    [Fact]
    public async Task PersistedHabitAndChild_ReadEveryFieldThroughChatAndMcp()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Test", "readback@example.com").Value;
        var parent = Habit.Create(new HabitCreateParams(user.Id, "Routine", FrequencyUnit.Day, 1, Today,
            StartDate: Today.AddDays(-10))).Value;
        var child = Habit.Create(new HabitCreateParams(user.Id, "Read", FrequencyUnit.Day, 1, Today,
            ParentHabitId: parent.Id, StartDate: Today.AddDays(-5))).Value;
        var tag = Tag.Create(user.Id, "Health", "#123456").Value;
        parent.AddTag(tag);
        child.AddTag(tag);
        db.Users.Add(user);
        db.Habits.AddRange(parent, child);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var habits = new GenericRepository<Habit>(db);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        dates.GetUserWeekStartDayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(1);
        var logs = new HabitScheduleLogReader(db);
        var detail = new GetHabitByIdQueryHandler(habits, new GenericRepository<HabitLog>(db), dates);
        var schedule = new GetHabitScheduleQueryHandler(habits, logs, new HabitSchedulePageLoader(db), dates,
            Substitute.For<IUnitOfWork>());
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => detail.Handle(call.Arg<GetHabitByIdQuery>(), default));
        mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => schedule.Handle(call.Arg<GetHabitScheduleQuery>(), default));
        var mcp = new HabitTools(mediator, dates, Bridge());
        var chat = await new QueryHabitsTool(habits, new GenericRepository<User>(db), dates, logs)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { }), user.Id, default);
        var rows = chat.Payload.Should().BeOfType<HabitListCard>().Subject.Items;
        rows.Single(row => row.Id == child.Id.ToString()).Should().Match<HabitListCardItem>(row =>
            row.StartDate == child.StartDate && row.ParentHabitId == parent.Id && row.ParentTitle == parent.Title);
        rows.Should().OnlyContain(row => row.Tags != null && row.Tags.Single().Id == tag.Id && row.Tags.Single().Name == tag.Name);
        var detailText = await mcp.GetHabit(Principal(user.Id), parent.Id.ToString());
        var childText = await mcp.GetHabit(Principal(user.Id), child.Id.ToString());
        var listText = await mcp.ListHabits(Principal(user.Id), "2026-10-19", "2026-10-19");
        foreach (var text in new[] { chat.EntityName!, detailText, listText })
        {
            text.Should().Contain("Start date: 2026-10-09").And.Contain("Start date: 2026-10-14");
            text.Should().Contain($"Parent: Routine ({parent.Id})").And.Contain($"Health ({tag.Id})");
        }
        childText.Should().Contain($"Parent: Routine ({parent.Id})").And.Contain("Start date: 2026-10-14")
            .And.Contain($"Health ({tag.Id})");
        var filtered = await new QueryHabitsTool(habits, new GenericRepository<User>(db), dates, logs)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { search = "Read" }), user.Id, default);
        filtered.EntityName.Should().Contain($"Parent: Routine ({parent.Id})");
        var promptHabits = await habits.FindAsync(h => h.UserId == user.Id, q => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(q, h => h.Tags));
        var prompt = new ActiveHabitsSection().Build(new PromptContext(promptHabits, false, null, null, Today, null));
        prompt.Should().Contain($"\"Routine\" ({parent.Id})").And.Contain($"\"Health\" ({tag.Id})")
            .And.Contain("Start date: 2026-10-14");
    }

    [Fact]
    public async Task PersistedGoalLinks_ReadIdsAndTitlesThroughChatAndMcp()
    {
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        var user = User.Create("Test", "goal-readback@example.com").Value;
        var habit = Habit.Create(new HabitCreateParams(user.Id, "Read", FrequencyUnit.Day, 1, Today)).Value;
        var goal = Goal.Create(user.Id, "Books", 12, "books").Value;
        goal.AddHabit(habit);
        db.Users.Add(user);
        db.Goals.Add(goal);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var goals = new GenericRepository<Goal>(db);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Today);
        var syncer = Substitute.For<IGoalProgressReadSyncer>();
        syncer.ComputeFreshValuesAsync(user.Id, Today, Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, int>());
        var list = new GetGoalsQueryHandler(goals, dates, syncer);
        var detail = new GetGoalByIdQueryHandler(goals, dates);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetGoalsQuery>(), Arg.Any<CancellationToken>()).Returns(call => list.Handle(call.Arg<GetGoalsQuery>(), default));
        mediator.Send(Arg.Any<GetGoalByIdQuery>(), Arg.Any<CancellationToken>()).Returns(call => detail.Handle(call.Arg<GetGoalByIdQuery>(), default));
        var chat = await new QueryGoalsTool(goals, dates, syncer).ExecuteAsync(JsonSerializer.SerializeToElement(new { }), user.Id, default);
        chat.Payload.Should().BeOfType<GoalListCard>().Subject.Items.Single().LinkedHabits
            .Should().ContainSingle().Which.Should().Be(new LinkedHabitDto(habit.Id, habit.Title));
        var mcp = new GoalTools(mediator, Bridge());
        foreach (var text in new[] { chat.EntityName!, await mcp.ListGoals(Principal(user.Id)), await mcp.GetGoal(Principal(user.Id), goal.Id.ToString()) })
            text.Should().Contain($"Read ({habit.Id})");
        var promptGoal = await goals.FindAsync(g => g.Id == goal.Id, q => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.Include(q, g => g.Habits));
        new ActiveGoalsSection().Build(new PromptContext([], false, null, null, Today, null, promptGoal))
            .Should().Contain($"\"Read\" ({habit.Id})");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedCalendarSelection_ReadsModeAndMissingProviderIdThroughChatAndMcp(bool useDefault)
    {
        var user = User.Create("Test", "calendar-readback@example.com").Value;
        using var factory = new SqliteOrbitDbContextFactory();
        var db = factory.Context;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var users = new GenericRepository<User>(db);
        var work = new UnitOfWork(db, new DatabaseConnectionSettings());
        var write = new SetSelectedCalendarsCommandHandler(users, work);
        (await write.Handle(new SetSelectedCalendarsCommand(user.Id, ["owned", "removed"]), default)).IsSuccess.Should().BeTrue();
        if (useDefault)
            (await write.Handle(new SetSelectedCalendarsCommand(user.Id, []), default)).IsSuccess.Should().BeTrue();
        db.ChangeTracker.Clear();
        var gate = Substitute.For<IPayGateService>();
        gate.CanManageCalendar(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        gate.CanAccessCalendar(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var tokens = Substitute.For<IGoogleTokenService>();
        tokens.GetValidAccessTokenAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns("token");
        var provider = Substitute.For<ICalendarEventFetcher>();
        provider.ListCalendarsAsync("token", Arg.Any<CancellationToken>())
            .Returns(new List<CalendarListItem> { new("owned", "Owned", "owner", true, null, true) });
        var calendars = new GetUserCalendarsQueryHandler(users, gate, tokens, provider, Substitute.For<IUnitOfWork>(), NullLogger<GetUserCalendarsQueryHandler>.Instance);
        var state = new GetCalendarAutoSyncStateQueryHandler(users, gate);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetUserCalendarsQuery>(), Arg.Any<CancellationToken>()).Returns(call => calendars.Handle(call.Arg<GetUserCalendarsQuery>(), default));
        mediator.Send(Arg.Any<GetCalendarAutoSyncStateQuery>(), Arg.Any<CancellationToken>()).Returns(call => state.Handle(call.Arg<GetCalendarAutoSyncStateQuery>(), default));
        mediator.Send(Arg.Any<GetCalendarEventsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new List<CalendarEventItem>()));
        mediator.Send(Arg.Any<GetCalendarSyncSuggestionsQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(new List<CalendarSyncSuggestionItem>()));
        var chat = await new GetCalendarOverviewTool(mediator).ExecuteAsync(JsonSerializer.SerializeToElement(new { include_auto_sync_state = false }), user.Id, default);
        var mcp = await new CalendarTools(mediator, Bridge()).GetCalendarOverview(Principal(user.Id));
        foreach (var json in new[] { JsonSerializer.Serialize(chat.Payload), mcp })
        {
            var payload = JsonDocument.Parse(json).RootElement;
            payload.GetProperty("SelectionMode").GetString().Should().Be(useDefault ? "default" : "explicit");
            var ids = payload.GetProperty("SelectedCalendarIds");
            if (useDefault) ids.ValueKind.Should().Be(JsonValueKind.Null);
            else ids.EnumerateArray().Select(id => id.GetString()).Should().Equal("owned", "removed");
            payload.GetProperty("Calendars").GetArrayLength().Should().Be(1);
        }
    }
}
