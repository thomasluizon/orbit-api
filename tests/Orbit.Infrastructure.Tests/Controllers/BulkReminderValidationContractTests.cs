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
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class BulkReminderValidationContractTests
{
    public static TheoryData<string, bool> InvalidChildInputs()
    {
        var cases = new TheoryData<string, bool>();
        foreach (var mixed in new[] { false, true })
        {
            cases.Add("\"reminderTimes\":[-1]", mixed);
            cases.Add("\"reminderTimes\":[10081]", mixed);
            cases.Add("\"reminderTimes\":[15,15]", mixed);
            foreach (var token in new[] { "99", "2", "-1", "\"99\"", "\"2\"", "\"-1\"" })
            {
                cases.Add($$"""
                    "scheduledReminders":[{"when":{{token}},"time":"08:00"}]
                    """, mixed);
                cases.Add($$"""
                    "dueTime":"09:00","scheduledReminders":[{"when":{{token}},"time":"08:00"}]
                    """, mixed);
                cases.Add($$"""
                    "dueTime":"09:00","relativeReminders":[{"when":{{token}},"time":"08:00"}]
                    """, mixed);
            }
            cases.Add("""
                "dueTime":"09:00","relativeReminders":[{"minutesBefore":15,"when":"same_day","time":"08:00"}]
                """, mixed);
            cases.Add("""
                "dueTime":"09:00","relativeReminders":[{"when":"day_before"}]
                """, mixed);
            cases.Add("""
                "relativeReminders":[{"minutesBefore":15}]
                """, mixed);
            cases.Add("""
                "dueTime":"09:00","relativeReminders":[{"when":null,"time":"08:00"}]
                """, mixed);
            cases.Add("""
                "scheduledReminders":[{"when":"same_day","time":"08:00"},{"when":"same_day","time":"08:00"}]
                """, mixed);
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(InvalidChildInputs))]
    public async Task BulkCreate_InvalidLaterChild_ReturnsFailedItemWithoutPersistingItsFamily(string fields, bool mixed)
    {
        using var fixture = new Fixture();
        var affected = $$"""{"title":"Affected root","subHabits":[{"title":"Valid first child","scheduledReminders":[{"when":"same_day","time":"07:00"}]},{"title":"Invalid later child",{{fields}}}]}""";
        var items = mixed
            ? $$"""{"title":"Before","reminderTimes":[0,10080]},{{affected}},{"title":"After","reminderTimes":[45]}"""
            : affected;

        var result = await fixture.Create($$"""{"habits":[{{items}}]}""");

        using var readback = fixture.Database.CreateContext();
        var saved = await readback.Habits.AsNoTracking().ToListAsync();
        saved.Should().HaveCount(mixed ? 2 : 0);
        saved.Should().OnlyContain(h => h.ParentHabitId == null && (h.Title == "Before" || h.Title == "After"));
        var failed = result.Results[mixed ? 1 : 0];
        failed.Status.Should().Be(BulkItemStatus.Failed);
        failed.HabitId.Should().BeNull();
        failed.Field.Should().Be("SubHabits");
        failed.Error.Should().StartWith("Sub-habit 'Invalid later child' failed:");
        result.AppliedCount.Should().Be(mixed ? 2 : 0);
        result.TotalMatched.Should().Be(mixed ? 3 : 1);
        result.SkippedCount.Should().Be(1);
        result.Partial.Should().BeTrue();
        if (mixed)
        {
            result.Results[0].Status.Should().Be(BulkItemStatus.Success);
            result.Results[2].Status.Should().Be(BulkItemStatus.Success);
            result.Results[0].HabitId.Should().Be(saved.Single(h => h.Title == "Before").Id);
            result.Results[2].HabitId.Should().Be(saved.Single(h => h.Title == "After").Id);
            saved.Single(h => h.Title == "Before").ReminderTimes.Should().Equal(0, 10080);
            saved.Single(h => h.Title == "After").ReminderTimes.Should().Equal(45);
        }
        fixture.Database.Context.ChangeTracker.Entries<Habit>().Should()
            .NotContain(entry => entry.Entity.Title == "Affected root" || entry.Entity.ParentHabitId != null);
    }

    public static TheoryData<string, int[]> ValidReminderTimes => new()
    {
        { "null", [] },
        { "[]", [] },
        { "[0]", [0] },
        { "[10080]", [10080] },
        { "[10080,0,15]", [10080, 0, 15] }
    };

    [Theory]
    [MemberData(nameof(ValidReminderTimes))]
    public async Task BulkCreate_ValidChildOffsets_ReadBackWithIndependentReminderSettings(string offsets, int[] expected)
    {
        using var fixture = new Fixture();
        var result = await fixture.Create($$"""
            {"habits":[{"title":"Root","reminderEnabled":false,"reminderTimes":[30],"scheduledReminders":[{"when":"day_before","time":"07:00"}],"subHabits":[
                {"title":"Scheduled first child","reminderEnabled":true,"reminderTimes":[45],"scheduledReminders":[{"when":0,"time":"08:00"}]},
                {"title":"Offset child","reminderEnabled":true,"dueTime":"09:00","reminderTimes":{{offsets}},"relativeReminders":[
                    {"minutesBefore":-1439},{"minutesBefore":-1},{"minutesBefore":10080},{"when":1,"time":"08:00"}]}]}]}
            """);

        var item = result.Results.Should().ContainSingle().Subject;
        item.Status.Should().Be(BulkItemStatus.Success);
        item.HabitId.Should().NotBeNull();
        result.AppliedCount.Should().Be(1);
        result.TotalMatched.Should().Be(1);
        result.SkippedCount.Should().Be(0);
        result.Partial.Should().BeFalse();
        using var readback = fixture.Database.CreateContext();
        var root = await readback.Habits.AsNoTracking().SingleAsync(h => h.Id == item.HabitId);
        (await readback.Habits.CountAsync()).Should().Be(3);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(root.UserId, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var detail = await new GetHabitByIdQueryHandler(new GenericRepository<Habit>(readback),
            new GenericRepository<HabitLog>(readback), dates).Handle(new(root.UserId, root.Id), CancellationToken.None);

        detail.IsSuccess.Should().BeTrue();
        detail.Value.ReminderEnabled.Should().BeFalse();
        detail.Value.ReminderTimes.Should().Equal(30);
        detail.Value.ScheduledReminders.Should().Equal(new ScheduledReminderTime(ScheduledReminderWhen.DayBefore, new TimeOnly(7, 0)));
        detail.Value.Children.Should().HaveCount(2);
        var first = detail.Value.Children.Single(h => h.Title == "Scheduled first child");
        first.ReminderEnabled.Should().BeTrue();
        first.ReminderTimes.Should().Equal(45);
        first.ScheduledReminders.Should().Equal(new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8, 0)));
        var child = detail.Value.Children.Single(h => h.Title == "Offset child");
        child.ReminderEnabled.Should().BeTrue();
        child.ReminderTimes.Should().Equal(expected);
        child.RelativeReminders.Should().Equal(new RelativeReminderTime(-1439), new RelativeReminderTime(-1),
            new RelativeReminderTime(10080), new RelativeReminderTime(When: ScheduledReminderWhen.DayBefore, Time: new TimeOnly(8, 0)));
        using var output = JsonDocument.Parse(JsonSerializer.Serialize(detail.Value, ReminderInputContractTests.MvcOptions()));
        var outputChild = output.RootElement.GetProperty("children").EnumerateArray().Single(h => h.GetProperty("title").GetString() == "Offset child");
        outputChild.GetProperty("reminderTimes").EnumerateArray().Select(v => v.GetInt32()).Should().Equal(expected);
        var relative = outputChild.GetProperty("relativeReminders");
        relative[1].GetProperty("minutesBefore").GetInt32().Should().Be(-1);
        relative[3].GetProperty("when").GetString().Should().Be("DayBefore");
        relative[3].GetProperty("time").GetString().Should().Be("08:00:00");
    }

    public static TheoryData<string, ScheduledReminderWhen> ValidWhenInputs => new()
    {
        { "\"same_day\"", ScheduledReminderWhen.SameDay },
        { "\"day_before\"", ScheduledReminderWhen.DayBefore },
        { "\"SameDay\"", ScheduledReminderWhen.SameDay },
        { "\"DayBefore\"", ScheduledReminderWhen.DayBefore },
        { "\"sameday\"", ScheduledReminderWhen.SameDay },
        { "\"dAyBeFoRe\"", ScheduledReminderWhen.DayBefore },
        { "0", ScheduledReminderWhen.SameDay },
        { "1", ScheduledReminderWhen.DayBefore },
        { "\"0\"", ScheduledReminderWhen.SameDay },
        { "\"1\"", ScheduledReminderWhen.DayBefore }
    };

    [Theory]
    [MemberData(nameof(ValidWhenInputs))]
    public async Task BulkCreate_DirectChildren_PersistOwnScheduledFoldedAndRelativeSettings(string token, ScheduledReminderWhen expected)
    {
        using var fixture = new Fixture();
        var result = await fixture.Create($$"""
            {"habits":[{"title":"Root","reminderEnabled":false,"reminderTimes":[30],"subHabits":[
                {"title":"Scheduled","reminderEnabled":true,"reminderTimes":[45],"scheduledReminders":[{"when":{{token}},"time":"08:00"}]},
                {"title":"Folded","dueTime":"09:00","reminderEnabled":true,"reminderTimes":[60],"scheduledReminders":[{"when":{{token}},"time":"07:00"}]},
                {"title":"Relative","dueTime":"10:00","reminderEnabled":true,"reminderTimes":[],"relativeReminders":[{"when":{{token}},"time":"06:00"},{"minutesBefore":15}]},
                {"title":"Omitted"},
                {"title":"Null","reminderTimes":null,"scheduledReminders":null,"relativeReminders":null},
                {"title":"Empty","reminderTimes":[],"scheduledReminders":[],"relativeReminders":[]}
            ]}]}
            """);

        result.Results.Should().ContainSingle().Which.Status.Should().Be(BulkItemStatus.Success);
        result.AppliedCount.Should().Be(1);
        result.Partial.Should().BeFalse();
        using var readback = fixture.Database.CreateContext();
        var saved = await readback.Habits.AsNoTracking().ToListAsync();
        saved.Should().HaveCount(7);
        var root = saved.Single(h => h.ParentHabitId == null);
        root.ReminderEnabled.Should().BeFalse();
        root.ReminderTimes.Should().Equal(30);
        var children = saved.Where(h => h.ParentHabitId == root.Id).OrderBy(h => h.Position).ToList();
        children.Select(h => h.Position).Should().Equal(0, 1, 2, 3, 4, 5);
        children[0].ReminderEnabled.Should().BeTrue();
        children[0].ReminderTimes.Should().Equal(45);
        children[0].ScheduledReminders.Should().Equal(new ScheduledReminderTime(expected, new TimeOnly(8, 0)));
        children[1].ReminderEnabled.Should().BeTrue();
        children[1].ReminderTimes.Should().Equal(60);
        children[1].ScheduledReminders.Should().BeEmpty();
        children[1].RelativeReminders.Should().Equal(new RelativeReminderTime(When: expected, Time: new TimeOnly(7, 0)));
        children[2].ReminderEnabled.Should().BeTrue();
        children[2].ReminderTimes.Should().BeEmpty();
        children[2].RelativeReminders.Should().Equal(new RelativeReminderTime(When: expected, Time: new TimeOnly(6, 0)), new RelativeReminderTime(15));
        foreach (var child in children.Skip(3))
        {
            child.ReminderEnabled.Should().BeFalse();
            child.ScheduledReminders.Should().BeEmpty();
            child.RelativeReminders.Should().BeEmpty();
        }
        children[3].ReminderTimes.Should().Equal(15);
        children[4].ReminderTimes.Should().Equal(15);
        children[5].ReminderTimes.Should().BeEmpty();
        var options = ReminderInputContractTests.MvcOptions();
        using var output = JsonDocument.Parse(JsonSerializer.Serialize(children[0].ScheduledReminders, options));
        output.RootElement[0].GetProperty("when").GetString().Should().Be(expected.ToString());
    }

    [Theory]
    [InlineData("\"unknown\"")]
    [InlineData("\"SAME_DAY\"")]
    [InlineData("\"DAY_BEFORE\"")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("0.5")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void BulkCreate_MalformedDirectChildWhen_FailsMvcDeserialization(string token)
    {
        var options = ReminderInputContractTests.MvcOptions();
        foreach (var key in new[] { "scheduledReminders", "relativeReminders" })
        {
            var json = $$"""{"habits":[{"title":"Root","subHabits":[{"title":"Child","{{key}}":[{"when":{{token}},"time":"08:00"}]}]}]}""";
            Action deserialize = () => JsonSerializer.Deserialize<HabitsController.BulkCreateHabitsRequest>(json, options);
            deserialize.Should().Throw<JsonException>();
        }
        Action requiredNull = () => JsonSerializer.Deserialize<HabitsController.BulkCreateHabitsRequest>(
            """{"habits":[{"title":"Root","subHabits":[{"title":"Child","scheduledReminders":[{"when":null,"time":"08:00"}]}]}]}""", options);
        requiredNull.Should().Throw<JsonException>();
    }

    private sealed class Fixture : IDisposable
    {
        public SqliteOrbitDbContextFactory Database { get; } = new();
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly HabitsController _controller;

        public Fixture()
        {
            var db = Database.Context;
            var user = User.Create("User", "bulk-reminders@example.test").Value;
            db.Users.Add(user);
            db.SaveChanges();
            var dates = Substitute.For<IUserDateService>();
            dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
            var payGate = Substitute.For<IPayGateService>();
            payGate.CanCreateHabits(user.Id, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
            var handler = new BulkCreateHabitsCommandHandler(new(new GenericRepository<Habit>(db),
                    new GenericRepository<GoogleCalendarSyncSuggestion>(db), new GenericRepository<Tag>(db)),
                payGate, dates, new UnitOfWork(db, new DatabaseConnectionSettings()), _cache,
                NullLogger<BulkCreateHabitsCommandHandler>.Instance);
            var mediator = Substitute.For<IMediator>();
            mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var command = call.Arg<BulkCreateHabitsCommand>();
                new BulkCreateHabitsCommandValidator().Validate(command).IsValid.Should().BeTrue();
                return handler.Handle(command, call.Arg<CancellationToken>());
            });
            _controller = new HabitsController(mediator, NullLogger<HabitsController>.Instance, dates)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"))
                    }
                }
            };
        }

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
