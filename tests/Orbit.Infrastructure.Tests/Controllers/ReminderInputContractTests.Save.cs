using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Tests.Controllers;

public partial class ReminderInputContractTests
{
    [Theory]
    [InlineData("same_day", ScheduledReminderWhen.SameDay, false)]
    [InlineData("day_before", ScheduledReminderWhen.DayBefore, false)]
    [InlineData("SameDay", ScheduledReminderWhen.SameDay, false)]
    [InlineData("DayBefore", ScheduledReminderWhen.DayBefore, false)]
    [InlineData("same_day", ScheduledReminderWhen.SameDay, true)]
    [InlineData("day_before", ScheduledReminderWhen.DayBefore, true)]
    [InlineData("SameDay", ScheduledReminderWhen.SameDay, true)]
    [InlineData("DayBefore", ScheduledReminderWhen.DayBefore, true)]
    public async Task MvcRequests_SaveAndReadBackThroughRealHandlers(string name, ScheduledReminderWhen expected, bool relative)
    {
        using var fixture = new SaveFixture();
        var options = MvcOptions();
        var fields = relative
            ? $$"""
                "dueTime":"09:00","relativeReminders":[{"when":"{{name}}","time":"08:00"},{"minutesBefore":15}]
                """
            : $$"""
                "scheduledReminders":[{"when":"{{name}}","time":"08:00"}]
                """;
        var json = $$"""{"title":"Reminder","reminderEnabled":true,"reminderTimes":[30],{{fields}}}""";

        var create = JsonSerializer.Deserialize<HabitsController.CreateHabitRequest>(json, options)!;
        (await fixture.Controller.CreateHabit(create, CancellationToken.None)).Should().BeOfType<CreatedAtActionResult>();
        var root = fixture.Added.Should().ContainSingle().Which;
        await fixture.AssertReadBack(root, expected, relative);

        root.Update(new HabitUpdateParams(root.Title, root.Description, root.FrequencyUnit, root.FrequencyQuantity,
            null, false, null, ReminderEnabled: false, ReminderTimes: [], ScheduledReminders: [], RelativeReminders: []))
            .IsSuccess.Should().BeTrue();
        var update = JsonSerializer.Deserialize<HabitsController.UpdateHabitRequest>(json, options)!;
        (await fixture.Controller.UpdateHabit(root.Id, update, CancellationToken.None)).Should().BeOfType<NoContentResult>();
        await fixture.AssertReadBack(root, expected, relative);

        var sub = JsonSerializer.Deserialize<HabitsController.CreateSubHabitRequest>(json, options)!;
        (await fixture.Controller.CreateSubHabit(root.Id, sub, CancellationToken.None)).Should().BeOfType<CreatedAtActionResult>();
        var child = fixture.Added.Single(h => h.ParentHabitId == root.Id);
        await fixture.AssertReadBack(child, expected, relative);

        var bulkJson = $$"""{"habits":[{"title":"Bulk root","reminderEnabled":true,"reminderTimes":[30],{{fields}},"subHabits":[{{json}}]}]}""";
        var bulk = JsonSerializer.Deserialize<HabitsController.BulkCreateHabitsRequest>(bulkJson, options)!;
        var response = (await fixture.Controller.BulkCreate(bulk, CancellationToken.None))
            .Should().BeOfType<ObjectResult>().Subject;
        response.StatusCode.Should().Be(StatusCodes.Status201Created);
        var bulkResult = response.Value.Should().BeOfType<BulkCreateResult>().Subject;
        bulkResult.Results.Single().Status.Should().Be(BulkItemStatus.Success);
        fixture.Added.Should().HaveCount(4);
        var bulkRoot = fixture.Added.Single(h => h.Id == bulkResult.Results.Single().HabitId);
        var bulkChild = fixture.Added.Single(h => h.ParentHabitId == bulkRoot.Id);
        bulkChild.Position.Should().Be(0);
        await fixture.AssertReadBack(bulkRoot, expected, relative);
        await fixture.AssertReadBack(bulkChild, expected, relative);
        await fixture.UnitOfWork.Received(4).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private sealed class SaveFixture : IDisposable
    {
        private static readonly DateOnly Today = new(2026, 4, 3);
        private readonly Guid _userId = Guid.NewGuid();
        private readonly IGenericRepository<Habit> _habits = Substitute.For<IGenericRepository<Habit>>();
        private readonly IGenericRepository<HabitLog> _logs = Substitute.For<IGenericRepository<HabitLog>>();
        private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();
        public List<Habit> Added { get; } = [];
        public HabitsController Controller { get; }

        public SaveFixture()
        {
            _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Today);
            _habits.AddAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Added.Add(call.Arg<Habit>());
                return Task.CompletedTask;
            });
            _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Added.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
            _habits.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                    Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
                .Returns(call => Added.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
            _habits.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
                    Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
                .Returns(call => Added.SingleOrDefault(call.Arg<Expression<Func<Habit, bool>>>().Compile()));
            UnitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Guid>>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<Func<CancellationToken, Task<Result<Guid>>>>()(call.Arg<CancellationToken>()));
            UnitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<Func<CancellationToken, Task<Result>>>()(call.Arg<CancellationToken>()));
            UnitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<BulkCreateResult>>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.Arg<Func<CancellationToken, Task<Result<BulkCreateResult>>>>()(call.Arg<CancellationToken>()));
            var payGate = Substitute.For<IPayGateService>();
            payGate.CanCreateHabits(_userId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
            var tags = Substitute.For<IGenericRepository<Tag>>();
            var goals = Substitute.For<IGenericRepository<Goal>>();
            var goalCompletion = Substitute.For<IGoalCompletionService>();
            var config = Substitute.For<IAppConfigService>();
            config.GetAsync(AppConfigKeys.MaxHabitDepth, AppConstants.MaxHabitDepth, Arg.Any<CancellationToken>())
                .Returns(AppConstants.MaxHabitDepth);
            var create = new CreateHabitCommandHandler(new(_habits, tags, goals), _dates, payGate,
                Substitute.For<IGamificationService>(), goalCompletion, UnitOfWork, _cache,
                Substitute.For<ILogger<CreateHabitCommandHandler>>());
            var update = new UpdateHabitCommandHandler(_habits, Substitute.For<IGenericRepository<SentReminder>>(),
                goals, payGate, goalCompletion, _dates, UnitOfWork, _cache);
            var sub = new CreateSubHabitCommandHandler(_habits, tags, _dates, UnitOfWork, config, payGate, _cache);
            var bulk = new BulkCreateHabitsCommandHandler(new(_habits, Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>(), tags),
                payGate, _dates, UnitOfWork, _cache, config);
            var mediator = Substitute.For<IMediator>();
            mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var command = call.Arg<CreateHabitCommand>();
                new CreateHabitCommandValidator().Validate(command).IsValid.Should().BeTrue();
                return create.Handle(command, call.Arg<CancellationToken>());
            });
            mediator.Send(Arg.Any<UpdateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
            {
                var command = call.Arg<UpdateHabitCommand>();
                (await new UpdateHabitCommandValidator(_habits).ValidateAsync(command)).IsValid.Should().BeTrue();
                return await update.Handle(command, call.Arg<CancellationToken>());
            });
            mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var command = call.Arg<CreateSubHabitCommand>();
                new CreateSubHabitCommandValidator().Validate(command).IsValid.Should().BeTrue();
                return sub.Handle(command, call.Arg<CancellationToken>());
            });
            mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var command = call.Arg<BulkCreateHabitsCommand>();
                new BulkCreateHabitsCommandValidator().Validate(command).IsValid.Should().BeTrue();
                return bulk.Handle(command, call.Arg<CancellationToken>());
            });
            Controller = new HabitsController(mediator, Substitute.For<ILogger<HabitsController>>(), _dates)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"))
                    }
                }
            };
        }

        public async Task AssertReadBack(Habit saved, ScheduledReminderWhen when, bool relative)
        {
            saved.ReminderEnabled.Should().BeTrue();
            saved.ReminderTimes.Should().Equal(30);
            var expected = new ScheduledReminderTime(when, new TimeOnly(8, 0));
            saved.GetScheduledRemindersForLegacyClients().Should().Equal(expected);
            if (relative)
                saved.RelativeReminders.Should().Equal(RelativeReminderTime.FromScheduled(expected), new RelativeReminderTime(15));
            else
                saved.ScheduledReminders.Should().Equal(expected);
            var result = await new GetHabitByIdQueryHandler(_habits, _logs, _dates)
                .Handle(new(_userId, saved.Id), CancellationToken.None);
            result.IsSuccess.Should().BeTrue();
            result.Value.ScheduledReminders.Should().Equal(expected);
            result.Value.RelativeReminders.Should().Equal(saved.RelativeReminders);
            var options = MvcOptions();
            var json = JsonSerializer.Serialize(result.Value, options);
            json.Should().Be(JsonSerializer.Serialize(result.Value, BaselineOptions(options)));
            using var document = JsonDocument.Parse(json);
            document.RootElement.GetProperty("scheduledReminders")[0].GetRawText()
                .Should().Be($$"""{"when":"{{when}}","time":"08:00:00"}""");
            if (relative)
                document.RootElement.GetProperty("relativeReminders")[1].GetRawText()
                    .Should().Be("""{"minutesBefore":15,"when":null,"time":null}""");
        }

        public void Dispose() => _cache.Dispose();
    }
}
