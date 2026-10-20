using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class BulkSubtreePersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MvcRecursiveTree_PersistsCompleteFieldsTagsAncestryAndSyncReview(bool timed)
    {
        using var fixture = new Fixture();
        var db = fixture.Database.Context;
        var existing = Tag.Create(fixture.UserId, "Health", "#123456").Value;
        db.Tags.Add(existing);
        var foreignUser = User.Create("Other", "foreign-bulk@example.test").Value;
        db.Users.Add(foreignUser);
        var foreign = Tag.Create(foreignUser.Id, "New", "#abcdef").Value;
        db.Tags.Add(foreign);
        var suggestion = GoogleCalendarSyncSuggestion.Create(fixture.UserId, "leaf-event", "Event",
            new DateTime(2026, 4, 3, 9, 0, 0, DateTimeKind.Utc), "{}",
            new DateTime(2026, 4, 2, 9, 0, 0, DateTimeKind.Utc));
        db.GoogleCalendarSyncSuggestions.Add(suggestion);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var timeFields = timed ? "\"dueTime\":\"09:00\",\"relativeReminders\":[{\"minutesBefore\":15}]," : "";
        var result = await fixture.Create($$"""
            {"fromSyncReview":true,"habits":[{"title":"Root","frequencyUnit":"Day","frequencyQuantity":1,
              "days":["Monday"],"intervalWeeks":2,"dueDate":"2026-04-06","subHabits":[
                {"title":"Child","subHabits":[
                  {"title":" Leaf ","description":" Description ","frequencyUnit":"Week","frequencyQuantity":2,
                   "days":[],"isBadHabit":true,"isGeneral":false,"isFlexible":true,"dueDate":"2026-04-07",
                   {{timeFields}}"dueEndTime":"10:00","endDate":"2026-05-01","reminderEnabled":true,"reminderTimes":[0,10080],
                   "scheduledReminders":[{"when":"day_before","time":"08:00"}],
                   "checklistItems":[{"text":"Checked","isChecked":true}],"googleEventId":"leaf-event","emoji":"⭐",
                   "tags":["health","New","new"]},
                  {"title":"Inherited","tags":["NEW"]}]},
                {"title":"Second child","tags":["Health"]}]}]}
            """);

        result.AppliedCount.Should().Be(1);
        result.TotalMatched.Should().Be(1);
        result.Partial.Should().BeFalse();
        using var readback = fixture.Database.CreateContext();
        var saved = await readback.Habits.Include(h => h.Tags).OrderBy(h => h.Position).ToListAsync();
        saved.Should().HaveCount(5).And.OnlyContain(h => h.UserId == fixture.UserId);
        var root = saved.Single(h => h.ParentHabitId == null);
        var child = saved.Single(h => h.Title == "Child");
        var leaf = saved.Single(h => h.Title == "Leaf");
        child.ParentHabitId.Should().Be(root.Id);
        leaf.ParentHabitId.Should().Be(child.Id);
        leaf.Position.Should().Be(0);
        saved.Single(h => h.Title == "Inherited").Position.Should().Be(1);
        saved.Single(h => h.Title == "Second child").Position.Should().Be(1);
        foreach (var node in saved.Where(h => h.Title is "Root" or "Child" or "Inherited" or "Second child"))
        {
            node.FrequencyUnit.Should().Be(FrequencyUnit.Day);
            node.FrequencyQuantity.Should().Be(1);
            node.IntervalWeeks.Should().Be(2);
            node.Days.Should().Equal(DayOfWeek.Monday);
            node.DueDate.Should().Be(new DateOnly(2026, 4, 6));
        }
        leaf.Description.Should().Be("Description");
        leaf.Emoji.Should().Be("⭐");
        leaf.FrequencyUnit.Should().Be(FrequencyUnit.Week);
        leaf.FrequencyQuantity.Should().Be(2);
        leaf.IntervalWeeks.Should().BeNull();
        leaf.IsFlexible.Should().BeTrue();
        leaf.IsBadHabit.Should().BeTrue();
        leaf.IsGeneral.Should().BeFalse();
        leaf.DueDate.Should().Be(new DateOnly(2026, 4, 7));
        leaf.DueTime.Should().Be(timed ? new TimeOnly(9, 0) : null);
        leaf.DueEndTime.Should().Be(new TimeOnly(10, 0));
        leaf.EndDate.Should().Be(new DateOnly(2026, 5, 1));
        leaf.ChecklistItems.Should().Equal(new ChecklistItem("Checked", true));
        leaf.GoogleEventId.Should().Be("leaf-event");
        leaf.ReminderEnabled.Should().BeTrue();
        leaf.ReminderTimes.Should().Equal(0, 10080);
        leaf.GetScheduledRemindersForLegacyClients().Should().Equal(new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(8, 0)));
        leaf.RelativeReminders.Should().Equal(timed ? [new RelativeReminderTime(15), new(When: ScheduledReminderWhen.DayBefore, Time: new(8, 0))] : []);
        leaf.Tags.Select(t => t.Name).Should().BeEquivalentTo("Health", "New");
        leaf.Tags.Single(t => t.Name == "Health").Id.Should().Be(existing.Id);
        leaf.Tags.Should().OnlyContain(t => t.UserId == fixture.UserId);
        leaf.Tags.Should().NotContain(t => t.Id == foreign.Id);
        (await readback.Tags.CountAsync(t => t.UserId == fixture.UserId)).Should().Be(2);
        var imported = await readback.GoogleCalendarSyncSuggestions.SingleAsync();
        imported.ImportedHabitId.Should().Be(leaf.Id);
        imported.ImportedAtUtc.Should().NotBeNull();
        var detail = await new GetHabitByIdQueryHandler(new GenericRepository<Habit>(readback),
            new GenericRepository<HabitLog>(readback), fixture.Dates)
            .Handle(new(fixture.UserId, leaf.Id), CancellationToken.None);
        detail.IsSuccess.Should().BeTrue();
        detail.Value.DueEndTime.Should().Be(leaf.DueEndTime);
        detail.Value.ChecklistItems.Should().Equal(leaf.ChecklistItems);
        (detail.Value.Tags ?? []).Select(t => t.Name).Should().BeEquivalentTo("Health", "New");
    }

    [Theory]
    [MemberData(nameof(BulkReminderValidationContractTests.InvalidChildInputs), MemberType = typeof(BulkReminderValidationContractTests))]
    public async Task MvcInvalidLaterGrandchild_LeavesNoTrackedPrefixOrTagJoins(string fields, bool mixed)
    {
        using var fixture = new Fixture();
        var affected = $$"""
            {"title":"Affected","tags":["Root tag"],"subHabits":[
              {"title":"Earlier sibling","tags":["Sibling tag"]},
              {"title":"Later sibling","subHabits":[{"title":"Earlier leaf","tags":["Leaf tag"]},
                 {"title":"Invalid later leaf",{{fields}}}]}]}
            """;
        var items = mixed ? $$"""{"title":"Before"},{{affected}},{"title":"After"}""" : affected;

        var result = await fixture.Create($$"""{"habits":[{{items}}]}""");

        result.AppliedCount.Should().Be(mixed ? 2 : 0);
        result.SkippedCount.Should().Be(1);
        result.Results[mixed ? 1 : 0].Field.Should().Be("SubHabits");
        fixture.Database.Context.ChangeTracker.Entries().Should().NotContain(entry => entry.Entity is Tag);
        fixture.Database.Context.ChangeTracker.Entries<Habit>().Should()
            .OnlyContain(entry => entry.Entity.Title == "Before" || entry.Entity.Title == "After");
        await fixture.UnitOfWork.SaveChangesAsync();
        using var readback = fixture.Database.CreateContext();
        (await readback.Habits.CountAsync()).Should().Be(mixed ? 2 : 0);
        (await readback.Tags.CountAsync()).Should().Be(0);
        (await readback.Set<Dictionary<string, object>>("HabitTags").CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("habit")]
    [InlineData("tag")]
    [InlineData("suggestion")]
    [InlineData("cancel")]
    public async Task AttachmentOrPostSaveFailure_PropagatesAndTransactionDiscardsAllTrackedWork(string failure)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var db = fixture.Database.Context;
        var realHabits = new GenericRepository<Habit>(db);
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => realHabits.FindAsync(call.Arg<Expression<Func<Habit, bool>>>(), call.Arg<CancellationToken>()));
        var added = 0;
        habits.AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await realHabits.AddAsync(call.Arg<Habit>(), call.Arg<CancellationToken>());
            if (++added == 2)
            {
                if (failure == "habit")
                    throw new InvalidOperationException("Repository failure");
                if (failure == "cancel")
                    cancellation.Cancel();
            }
        });
        var realTags = new GenericRepository<Tag>(db);
        var tags = Substitute.For<IGenericRepository<Tag>>();
        tags.FindTrackedAsync(Arg.Any<Expression<Func<Tag, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => realTags.FindTrackedAsync(call.Arg<Expression<Func<Tag, bool>>>(), call.Arg<CancellationToken>()));
        tags.AddAsync(Arg.Any<Tag>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await realTags.AddAsync(call.Arg<Tag>(), call.Arg<CancellationToken>());
            if (failure == "tag")
                throw new InvalidOperationException("Tag repository failure");
        });
        var suggestions = Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>();
        suggestions.FindTrackedAsync(Arg.Any<Expression<Func<GoogleCalendarSyncSuggestion, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<GoogleCalendarSyncSuggestion>>(new InvalidOperationException("Suggestion transport failure")));
        var handler = fixture.Handler(new(habits, suggestions, tags));
        var tree = new BulkHabitItem("Root", null, null, null, SubHabits:
            [new("Child", null, null, null, Tags: ["New"], SubHabits: [new("Leaf", null, null, null, GoogleEventId: "event")])]);

        var action = () => handler.Handle(new(fixture.UserId, [tree], FromSyncReview: failure == "suggestion"), cancellation.Token);

        if (failure == "cancel")
            await action.Should().ThrowAsync<OperationCanceledException>();
        else
            await action.Should().ThrowAsync<InvalidOperationException>();
        db.ChangeTracker.Entries().Should().BeEmpty();
        await fixture.UnitOfWork.SaveChangesAsync();
        using var readback = fixture.Database.CreateContext();
        (await readback.Habits.CountAsync()).Should().Be(0);
        (await readback.Tags.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public async Task DescendantCount_DoesNotIncreaseHabitOrTagSelects(int siblings)
    {
        var counter = new CountingDbCommandInterceptor();
        using var fixture = new Fixture(counter);
        var tree = new BulkHabitItem("Root", null, null, null, SubHabits: Enumerable.Range(0, siblings)
            .Select(_ => new BulkHabitItem("Child", null, null, null, SubHabits: [new("Leaf", null, null, null, Tags: ["Shared"])])).ToList());
        counter.Reset();

        var result = await fixture.Handler().Handle(new(fixture.UserId, [tree]), CancellationToken.None);

        result.Value.AppliedCount.Should().Be(1);
        counter.SelectCommandCount.Should().Be(2);
        using var readback = fixture.Database.CreateContext();
        (await readback.Habits.CountAsync()).Should().Be(1 + 2 * siblings);
        (await readback.Tags.CountAsync()).Should().Be(1);
    }

    private sealed class Fixture : IDisposable
    {
        public SqliteOrbitDbContextFactory Database { get; }
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        public Guid UserId { get; }
        public IUserDateService Dates { get; } = Substitute.For<IUserDateService>();
        private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
        private readonly IAppConfigService _config = Substitute.For<IAppConfigService>();
        public UnitOfWork UnitOfWork { get; }
        private readonly HabitsController _controller;

        public Fixture(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
        {
            Database = new(interceptors);
            var db = Database.Context;
            var user = User.Create("User", "bulk-subtree@example.test").Value;
            UserId = user.Id;
            db.Users.Add(user);
            db.SaveChanges();
            db.ChangeTracker.Clear();
            Dates.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
            _payGate.CanCreateHabits(UserId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Orbit.Domain.Common.Result.Success());
            _config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
            UnitOfWork = new(db, new DatabaseConnectionSettings());
            var mediator = Substitute.For<IMediator>();
            mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var command = call.Arg<BulkCreateHabitsCommand>();
                new BulkCreateHabitsCommandValidator().Validate(command).IsValid.Should().BeTrue();
                return Handler().Handle(command, call.Arg<CancellationToken>());
            });
            _controller = new(mediator, NullLogger<HabitsController>.Instance, Dates)
            {
                ControllerContext = new()
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "Test"))
                    }
                }
            };
        }

        public BulkCreateHabitsCommandHandler Handler(BulkCreateHabitsRepositories? repos = null) => new(
            repos ?? new(new GenericRepository<Habit>(Database.Context), new GenericRepository<GoogleCalendarSyncSuggestion>(Database.Context),
                new GenericRepository<Tag>(Database.Context)), _payGate, Dates, UnitOfWork, _cache, _config);

        public async Task<BulkCreateResult> Create(string json)
        {
            var request = JsonSerializer.Deserialize<HabitsController.BulkCreateHabitsRequest>(json, ReminderInputContractTests.MvcOptions())!;
            var response = (await _controller.BulkCreate(request, CancellationToken.None)).Should().BeOfType<ObjectResult>().Subject;
            response.StatusCode.Should().Be(StatusCodes.Status201Created);
            return response.Value.Should().BeOfType<BulkCreateResult>().Subject;
        }

        public void Dispose()
        {
            _cache.Dispose();
            Database.Dispose();
        }
    }
}
