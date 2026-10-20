using System.Globalization;
using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Mcp;

public class HabitToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly IAgentOperationExecutor _executor = Substitute.For<IAgentOperationExecutor>();
    private readonly HabitTools _tools;
    private readonly ClaimsPrincipal _user;

    public HabitToolsTests()
    {
        _tools = new HabitTools(_mediator, _userDateService, new McpExecutorBridge(_executor));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static RetrospectiveMetrics EmptyMetrics() =>
        new(0, 0, 0, 0, 0, 0, 0, 0, new int[7], [], []);

    private void StubExecutor(AgentOperationStatus status, string? targetId = null, string? targetName = null,
        string? policyReason = null, object? payload = null, Guid? pendingOperationId = null)
    {
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "operation",
            "operation",
            AgentRiskClass.Low,
            AgentConfirmationRequirement.None,
            status,
            Summary: "summary",
            TargetId: targetId,
            TargetName: targetName,
            PolicyReason: policyReason,
            PendingOperationId: pendingOperationId,
            Payload: payload));

        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);
    }

    private async Task<AgentExecuteOperationRequest> CapturedRequestAsync(Func<Task> act)
    {
        await act();
        var calls = _executor.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAgentOperationExecutor.ExecuteAsync))
            .ToList();
        calls.Should().NotBeEmpty();
        return (AgentExecuteOperationRequest)calls[^1].GetArguments()[0]!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task UpdateHabit_DueTime_OmitKeepsAndEmptyClears(string? dueTime)
    {
        var userId = Guid.Parse(_user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var today = new DateOnly(2026, 10, 19);
        var habit = Habit.Create(new HabitCreateParams(userId, "Read", FrequencyUnit.Day, 1, today,
            DueTime: new TimeOnly(9, 0))).Value;
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(habit);
        _userDateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<string>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<string>>>>()(call.Arg<CancellationToken>()));
        var handler = new UpdateHabitCommandHandler(repository, Substitute.For<IGenericRepository<SentReminder>>(),
            Substitute.For<IGenericRepository<Goal>>(), Substitute.For<IPayGateService>(),
            Substitute.For<IGoalCompletionService>(), _userDateService, unitOfWork,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        _mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
        var tool = new UpdateHabitTool(repository, _userDateService, _mediator);
        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            if (dueTime is null)
                request.Arguments.TryGetProperty("due_time", out _).Should().BeFalse();
            else
                request.Arguments.GetProperty("due_time").ValueKind.Should().Be(JsonValueKind.Null);
            var result = await tool.ExecuteAsync(request.Arguments, userId, call.Arg<CancellationToken>());
            result.Success.Should().BeTrue();
            return new AgentExecuteOperationResponse(new AgentOperationResult("update_habit", "update_habit",
                AgentRiskClass.Low, AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded));
        });
        var result = await _tools.UpdateHabit(_user, habit.Id.ToString(), dueTime: dueTime);
        result.Should().Be($"Updated habit {habit.Id}");
        habit.DueTime.Should().Be(dueTime is null ? new TimeOnly(9, 0) : null);
    }

    [Theory]
    [InlineData("update", "relative_reminders")]
    [InlineData("child", "relative_reminders")]
    [InlineData("child", "scheduled_reminders")]
    [InlineData("child", "checklist_items")]
    public async Task InvalidJson_ReturnsValidationErrorWithoutExecuting(string operation, string field)
    {
        var result = operation == "update"
            ? await _tools.UpdateHabit(_user, Guid.NewGuid().ToString(), relativeReminders: "[")
            : await _tools.CreateSubHabit(_user, Guid.NewGuid().ToString(), "Child",
                relativeReminders: field == "relative_reminders" ? "[" : null,
                scheduledReminders: field == "scheduled_reminders" ? "[" : null,
                checklistItems: field == "checklist_items" ? "[" : null);
        result.Should().StartWith("Error: ").And.Contain(field);
        await _executor.DidNotReceive().ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateSubHabit_OmittedFrequency_InheritsThroughMcpAndCommand()
    {
        var userId = Guid.Parse(_user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var today = new DateOnly(2026, 10, 19);
        var parent = Habit.Create(new HabitCreateParams(userId, "Parent", FrequencyUnit.Week, 3, today, IntervalWeeks: 2)).Value;
        var habits = Substitute.For<IGenericRepository<Habit>>();
        habits.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>()).Returns(parent);
        var gate = Substitute.For<IPayGateService>();
        gate.CanCreateSubHabits(userId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync("MaxHabitDepth", 5, Arg.Any<CancellationToken>()).Returns(5);
        _userDateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        var handler = new CreateSubHabitCommandHandler(habits, Substitute.For<IGenericRepository<Tag>>(), gate,
            _userDateService, Substitute.For<IUnitOfWork>(), config,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<CreateSubHabitCommand>(), call.Arg<CancellationToken>()));
        var tool = new CreateSubHabitTool(_mediator);
        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            request.Arguments.TryGetProperty("frequency_unit", out _).Should().BeFalse();
            var result = await tool.ExecuteAsync(request.Arguments, userId, call.Arg<CancellationToken>());
            result.Success.Should().BeTrue();
            return new AgentExecuteOperationResponse(new AgentOperationResult("create_sub_habit", "create_sub_habit",
                AgentRiskClass.Low, AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, TargetId: result.EntityId));
        });
        await _tools.CreateSubHabit(_user, parent.Id.ToString(), "Child");
        await habits.Received(1).AddAsync(Arg.Is<Habit>(h => h.FrequencyUnit == FrequencyUnit.Week
            && h.FrequencyQuantity == 3 && h.IntervalWeeks == 2), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Interval weeks: 2")]
    [InlineData("Due end time: 15:00")]
    [InlineData("Reminders: enabled")]
    [InlineData("Reminder minutes: 15")]
    [InlineData("Slip alerts: enabled")]
    [InlineData("End date: 2026-12-31")]
    [InlineData("[x] Shoes")]
    [InlineData("Relative reminders: 30 minutes before")]
    [InlineData("Scheduled reminders: same_day 08:00")]
    public async Task ReadSurfaces_PrintEditableFields(string expected)
    {
        var date = new DateOnly(2026, 10, 19);
        var checklist = new[] { new ChecklistItem("Shoes", true) };
        var scheduled = new[] { new ScheduledReminderTime(ScheduledReminderWhen.SameDay, new TimeOnly(8, 0)) };
        var relative = new[] { new RelativeReminderTime(MinutesBefore: 30) };
        var detail = new HabitDetailResponse(Guid.NewGuid(), "Run", "Outside", FrequencyUnit.Day, 1,
            false, false, false, false, date, new TimeOnly(9, 0), new TimeOnly(15, 0), new DateOnly(2026, 12, 31),
            [DayOfWeek.Monday], null, true, [15], scheduled, checklist, DateTime.UnixEpoch, [],
            IntervalWeeks: 2, SlipAlertEnabled: true, RelativeReminders: relative);
        var child = new HabitChildResponse(Guid.NewGuid(), "Child", detail.Description, FrequencyUnit.Day, 1,
            false, false, false, false, detail.Days, date, detail.DueTime, detail.DueEndTime, detail.EndDate,
            null, checklist, false, [], IntervalWeeks: 2, Tags: [new HabitTagItem(Guid.NewGuid(), "Health", "#123456")],
            ReminderEnabled: true, ReminderTimes: [15], ScheduledReminders: scheduled, RelativeReminders: relative,
            SlipAlertEnabled: true);
        detail = detail with { Children = [child] };
        var scheduleChild = new HabitScheduleChildItem(child.Id, child.Title, child.Description, FrequencyUnit.Day, 1,
            false, false, false, false, child.Days, date, child.DueTime, child.DueEndTime, child.EndDate,
            [date], false, null, checklist, child.Tags!, [], false, null, null, false, [], IntervalWeeks: 2,
            ReminderEnabled: true, ReminderTimes: [15], ScheduledReminders: scheduled, RelativeReminders: relative,
            SlipAlertEnabled: true);
        var item = new HabitScheduleItem(detail.Id, detail.Title, detail.Description, FrequencyUnit.Day, 1,
            false, false, false, false, detail.Days, null, DateTime.UnixEpoch, date, detail.DueTime, detail.DueEndTime,
            detail.EndDate, [date], false, true, [15], scheduled, true, checklist, [], [], [scheduleChild], false,
            null, null, false, [], IntervalWeeks: 2, RelativeReminders: relative);
        _mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>()).Returns(Result.Success(detail));
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new PaginatedResponse<HabitScheduleItem>([item], 1, 50, 1, 1)));

        var detailText = await _tools.GetHabit(_user, detail.Id.ToString());
        var listText = await _tools.ListHabits(_user, "2026-10-19", "2026-10-20");
        detailText.Should().Contain(expected);
        listText.Should().Contain(expected);
        detailText.Split('\n').Single(line => line.Contains($"id: {child.Id}")).Should().Contain(expected).And.Contain("Health");
        listText.Split('\n').Single(line => line.Contains($"id: {child.Id}")).Should().Contain(expected).And.Contain("Health");
    }

    [Theory]
    [InlineData("dueEndTime", "due_end_time", "\"15:00\"")]
    [InlineData("relativeReminders", "relative_reminders", "[{\"minutes_before\":30}]")]
    [InlineData("endDate", "end_date", "\"2026-12-31\"")]
    [InlineData("checklistItems", "checklist_items", "[{\"text\":\"Shoes\",\"is_checked\":true}]")]
    public async Task CreateSubHabit_DomainFields_AreForwarded(string parameter, string field, string json)
    {
        StubExecutor(AgentOperationStatus.Succeeded);
        var method = typeof(HabitTools).GetMethod(nameof(HabitTools.CreateSubHabit))!;
        method.GetParameters().Should().Contain(p => p.Name == parameter);
        var values = method.GetParameters().Select(p => p.Name switch
        {
            "user" => (object)_user,
            "parentHabitId" => Guid.NewGuid().ToString(),
            "title" => "Child",
            _ when p.Name == parameter => json.StartsWith('[') ? json : JsonSerializer.Deserialize<string>(json),
            _ => p.DefaultValue
        }).ToArray();
        var request = await CapturedRequestAsync(() => method.Invoke(_tools, values) is Task<string> task
            ? task : throw new InvalidOperationException("Expected an asynchronous MCP create."));
        request.Arguments.GetProperty(field).GetRawText().Should().Be(json);
    }

    [Fact]
    public async Task UpdateHabit_NewFields_RouteThroughExecutorWithoutTitle()
    {
        StubExecutor(AgentOperationStatus.Succeeded);
        var request = await CapturedRequestAsync(() => _tools.UpdateHabit(_user, Guid.NewGuid().ToString(),
            dueEndTime: "15:00", isGeneral: true, slipAlertEnabled: true,
            relativeReminders: """[{"minutes_before":30}]"""));
        request.Arguments.TryGetProperty("title", out _).Should().BeFalse();
        request.Arguments.GetProperty("due_end_time").GetString().Should().Be("15:00");
        request.Arguments.GetProperty("is_general").GetBoolean().Should().BeTrue();
        request.Arguments.GetProperty("slip_alert_enabled").GetBoolean().Should().BeTrue();
        request.Arguments.GetProperty("relative_reminders")[0].GetProperty("minutes_before").GetInt32().Should().Be(30);
    }

    [Fact]
    public async Task UpdateHabit_ExplicitNullDeadline_ForwardsClear()
    {
        StubExecutor(AgentOperationStatus.Succeeded);
        var request = await CapturedRequestAsync(() => _tools.UpdateHabit(_user, Guid.NewGuid().ToString(), dueEndTime: null));
        request.Arguments.GetProperty("due_end_time").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task OmittedTitle_AndOtherOmittedFields_AreNotForwarded()
    {
        StubExecutor(AgentOperationStatus.Succeeded);
        var method = typeof(HabitTools).GetMethod(nameof(HabitTools.UpdateHabit))!;
        method.GetParameters().Single(p => p.Name == "title").IsOptional.Should().BeTrue();
        var arguments = method.GetParameters().Select(p => p.Name switch
        {
            "user" => (object)_user,
            "habitId" => Guid.NewGuid().ToString(),
            _ => p.HasDefaultValue ? p.DefaultValue : null
        }).ToArray();
        var request = await CapturedRequestAsync(() => method.Invoke(_tools, arguments) is Task<string> task
            ? task : throw new InvalidOperationException("Expected an asynchronous MCP update."));
        var args = request.Arguments;
        args.EnumerateObject().Select(p => p.Name).Should().Equal("habit_id");
    }

    [Fact]
    public async Task ListHabits_Success_ReturnsFormattedList()
    {
        var items = new List<HabitScheduleItem>
        {
            new(Guid.NewGuid(), "Read", null, FrequencyUnit.Day, 1, false, false, false, false,
                [], null, DateTime.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow), null, null, null,
                [], false, false, [], [], false, [], [], [], [], false, null, null, false, [], Emoji: "📚")
        };
        var paginated = new PaginatedResponse<HabitScheduleItem>(items, 1, 50, 1, 1);
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(paginated));

        var result = await _tools.ListHabits(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("Read");
        result.Should().Contain("Emoji: 📚");
        result.Should().Contain("page 1/1");
        result.Should().NotContain(" |  | Due:");
    }

    [Fact]
    public async Task ListHabits_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PaginatedResponse<HabitScheduleItem>>("Date range invalid"));

        var result = await _tools.ListHabits(_user, "2026-04-01", "2026-04-07");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task ListHabits_Empty_ReturnsNoHabitsMessage()
    {
        var paginated = new PaginatedResponse<HabitScheduleItem>([], 1, 50, 0, 0);
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(paginated));

        var result = await _tools.ListHabits(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("No habits found");
    }

    [Fact]
    public async Task GetHabit_Success_ReturnsDetail()
    {
        var habitId = Guid.NewGuid();
        var detail = new HabitDetailResponse(
            habitId, "Exercise", "Go for a run", FrequencyUnit.Day, 1,
            false, false, false, false,
            DateOnly.FromDateTime(DateTime.UtcNow), null, null, null,
            [], null, false, [], [], [], DateTime.UtcNow, [], Emoji: "🏃");

        _mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(detail));

        var result = await _tools.GetHabit(_user, habitId.ToString());

        result.Should().Contain("Exercise");
        result.Should().Contain("Emoji: 🏃");
        result.Should().Contain("Active");
    }

    [Fact]
    public async Task GetHabit_Dates_UseInvariantCalendar()
    {
        var habitId = Guid.NewGuid();
        var detail = new HabitDetailResponse(
            habitId, "Run", null, FrequencyUnit.Day, 1,
            false, false, false, false,
            new DateOnly(2026, 12, 31), null, null, null,
            [], null, false, [], [], [new Orbit.Domain.ValueObjects.ChecklistItem("Shoes", true)],
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), []);
        _mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(detail));
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var result = await _tools.GetHabit(_user, habitId.ToString());

            result.Should().Contain("Due Date: 12/31/2026");
            result.Should().Contain("Created: 2026-01-01");
            result.Should().Contain("Checklist: 1/1 items");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task GetHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitDetailResponse>("Habit not found"));

        var result = await _tools.GetHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateHabit_Success_RoutesThroughExecutorAndReturnsCreatedMessage()
    {
        var newId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: newId.ToString(), targetName: "New Habit");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.CreateHabit(
            _user,
            "New Habit",
            "2026-04-01",
            intervalWeeks: 2));

        request.OperationId.Should().Be("create_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        request.Arguments.GetProperty("interval_weeks").GetInt32().Should().Be(2);
        result.Should().Contain("Created habit 'New Habit'");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task CreateHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Title is required");

        var result = await _tools.CreateHabit(_user, "", "2026-04-01");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateHabit_ReadOnlyCredentialDenied_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Denied, policyReason: "read_only_credential");

        var result = await _tools.CreateHabit(_user, "New Habit", "2026-04-01");

        result.Should().StartWith("Error: ");
        result.Should().Contain("read_only_credential");
    }

    [Fact]
    public async Task CreateHabit_WithEmoji_ForwardsEmoji()
    {
        StubExecutor(AgentOperationStatus.Succeeded, targetId: Guid.NewGuid().ToString());

        var request = await CapturedRequestAsync(async () =>
            await _tools.CreateHabit(_user, "Read", "2026-04-01", emoji: "📚"));

        request.Arguments.GetProperty("emoji").GetString().Should().Be("📚");
    }

    [Fact]
    public async Task UpdateHabit_Success_RoutesThroughExecutor()
    {
        var habitId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: habitId.ToString());

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.UpdateHabit(
            _user,
            habitId.ToString(),
            "Updated Title",
            intervalWeeks: 3));

        request.OperationId.Should().Be("update_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        request.Arguments.GetProperty("interval_weeks").GetInt32().Should().Be(3);
        result.Should().Contain("Updated habit");
    }

    [Fact]
    public async Task UpdateHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.UpdateHabit(_user, Guid.NewGuid().ToString(), "Title");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task UpdateHabit_TitleOnly_PreservesDeadlineThroughExecutorAndTool()
    {
        var userId = Guid.Parse(_user.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var today = new DateOnly(2026, 4, 3);
        var scheduledReminders = new List<ScheduledReminderTime>
        {
            new(ScheduledReminderWhen.SameDay, new TimeOnly(14, 0))
        };
        var habit = Habit.Create(new HabitCreateParams(
            userId, "Read", null, null, today, DueEndTime: new TimeOnly(15, 0),
            ReminderEnabled: true, SlipAlertEnabled: true, ScheduledReminders: scheduledReminders)).Value;
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()).Returns(habit);
        _userDateService.GetUserTodayAsync(userId, Arg.Any<CancellationToken>()).Returns(today);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<string>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<string>>>>(0)(
                call.ArgAt<CancellationToken>(1)));
        var handler = new UpdateHabitCommandHandler(repository, Substitute.For<IGenericRepository<SentReminder>>(),
            Substitute.For<IGenericRepository<Goal>>(), Substitute.For<IPayGateService>(),
            Substitute.For<IGoalCompletionService>(), _userDateService, unitOfWork,
            new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        _mediator.Send(Arg.Any<UpdateHabitFieldsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.Handle(call.Arg<UpdateHabitFieldsCommand>(), call.Arg<CancellationToken>()));
        var tool = new UpdateHabitTool(repository, _userDateService, _mediator);
        var catalog = new AgentCatalogService([tool]);
        var policy = Substitute.For<IAgentPolicyEvaluator>();
        policy.Evaluate(Arg.Any<AgentPolicyEvaluationContext>()).Returns(new AgentPolicyDecision(
            AgentPolicyDecisionStatus.Allowed, catalog.GetCapabilityByChatTool(tool.Name)!));
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(
            Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        var stepUp = Substitute.For<IAgentStepUpAuthorizationBridge>();
        stepUp.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var executor = new AgentOperationExecutor(
            catalog, policy, Substitute.For<IAgentAuditService>(), ownership, stepUp,
            new AiToolRegistry([tool]), Substitute.For<IPendingOperationChangePreviewer>(),
            Substitute.For<IPendingAgentOperationStore>(), unitOfWork, NullLogger<AgentOperationExecutor>.Instance);
        var tools = new HabitTools(_mediator, _userDateService, new McpExecutorBridge(executor));

        var result = await tools.UpdateHabit(_user, habit.Id.ToString(), "Read daily");

        result.Should().Be($"Updated habit {habit.Id}");
        habit.Title.Should().Be("Read daily");
        habit.DueEndTime.Should().Be(new TimeOnly(15, 0));
        habit.SlipAlertEnabled.Should().BeTrue();
        habit.ScheduledReminders.Should().Equal(scheduledReminders);
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateHabit_WithEmoji_ForwardsValue()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var request = await CapturedRequestAsync(async () =>
            await _tools.UpdateHabit(_user, Guid.NewGuid().ToString(), "Read", emoji: "📚"));

        request.Arguments.GetProperty("emoji").GetString().Should().Be("📚");
    }

    [Fact]
    public async Task UpdateHabit_WithoutEmoji_OmitsField()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var request = await CapturedRequestAsync(async () =>
            await _tools.UpdateHabit(_user, Guid.NewGuid().ToString(), "Read"));

        request.Arguments.TryGetProperty("emoji", out _).Should().BeFalse();
    }

    [Fact]
    public async Task UpdateHabit_ExplicitNullEmoji_ForwardsNull()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var request = await CapturedRequestAsync(async () =>
            await _tools.UpdateHabit(_user, Guid.NewGuid().ToString(), "Read", emoji: null));

        request.Arguments.GetProperty("emoji").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task DeleteHabit_Success_RoutesThroughExecutor()
    {
        var habitId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: habitId.ToString());

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.DeleteHabit(_user, habitId.ToString()));

        request.OperationId.Should().Be("delete_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        result.Should().Contain("Deleted habit");
    }

    [Fact]
    public async Task DeleteHabit_PendingConfirmation_ReturnsConfirmationPrompt()
    {
        StubExecutor(AgentOperationStatus.PendingConfirmation, pendingOperationId: Guid.NewGuid());

        var result = await _tools.DeleteHabit(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Confirmation required");
        result.Should().Contain("confirm_agent_operation_v2");
    }

    [Fact]
    public async Task DeleteHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.DeleteHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task LogHabit_Success_RoutesThroughExecutor()
    {
        var habitId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: habitId.ToString());

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.LogHabit(_user, habitId.ToString()));

        request.OperationId.Should().Be("log_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        result.Should().Contain("Logged habit");
    }

    [Fact]
    public async Task LogHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.LogHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SkipHabit_Success_RoutesThroughExecutor()
    {
        var habitId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: habitId.ToString());

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.SkipHabit(_user, habitId.ToString()));

        request.OperationId.Should().Be("skip_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        result.Should().Contain("Skipped habit");
    }

    [Fact]
    public async Task UndoSkipHabit_RoutesThroughExecutorWithSkipId()
    {
        var habitId = Guid.NewGuid();
        var skipId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: habitId.ToString());

        var request = await CapturedRequestAsync(() => _tools.UndoSkipHabit(_user, habitId.ToString(), skipId.ToString()));

        request.OperationId.Should().Be("undo_skip_habit");
        request.Surface.Should().Be(AgentExecutionSurface.Mcp);
        request.Arguments.GetProperty("habit_id").GetString().Should().Be(habitId.ToString());
        request.Arguments.GetProperty("skip_id").GetString().Should().Be(skipId.ToString());
    }

    [Fact]
    public async Task SkipHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Not a recurring habit");

        var result = await _tools.SkipHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetHabitMetrics_Success_ReturnsFormattedMetrics()
    {
        var metrics = new HabitMetrics(5, 10, 0.85m, 0.75m, 50, DateOnly.FromDateTime(DateTime.UtcNow));
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(metrics));

        var result = await _tools.GetHabitMetrics(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Current Streak: 5 days");
        result.Should().Contain("Longest Streak: 10 days");
        result.Should().Contain("Total Completions: 50");
    }

    [Fact]
    public async Task GetHabitMetrics_Percentages_UseInvariantCulture()
    {
        var metrics = new HabitMetrics(5, 10, 0.85m, 0.75m, 50, new DateOnly(2026, 12, 31));
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(metrics));
        var previousCulture = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var result = await _tools.GetHabitMetrics(_user, Guid.NewGuid().ToString());

            result.Should().Contain("Weekly Completion Rate: 85 %");
            result.Should().Contain("Monthly Completion Rate: 75 %");
            result.Should().NotContain("85٪");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task GetHabitMetrics_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitMetrics>("Habit not found"));

        var result = await _tools.GetHabitMetrics(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DuplicateHabit_Success_RoutesThroughExecutor()
    {
        var newId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: newId.ToString());

        var habitId = Guid.NewGuid();
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.DuplicateHabit(_user, habitId.ToString()));

        request.OperationId.Should().Be("duplicate_habit");
        result.Should().Contain("Duplicated habit");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task DuplicateHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.DuplicateHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task MoveHabitParent_Success_WithParent_ReturnsMovedMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var habitId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.MoveHabitParent(_user, habitId.ToString(), parentId.ToString()));

        request.OperationId.Should().Be("move_habit_parent");
        result.Should().Contain("Moved habit");
        result.Should().Contain("under parent");
    }

    [Fact]
    public async Task MoveHabitParent_Success_NoParent_ReturnsPromotedMessage()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var result = await _tools.MoveHabitParent(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Promoted habit");
        result.Should().Contain("top-level");
    }

    [Fact]
    public async Task MoveHabitParent_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var result = await _tools.MoveHabitParent(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateSubHabit_Success_RoutesThroughExecutor()
    {
        var newId = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetId: newId.ToString(), targetName: "Sub Habit");

        var parentId = Guid.NewGuid();
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.CreateSubHabit(_user, parentId.ToString(), "Sub Habit"));

        request.OperationId.Should().Be("create_sub_habit");
        result.Should().Contain("Created sub-habit 'Sub Habit'");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task CreateSubHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Parent not found");

        var result = await _tools.CreateSubHabit(_user, Guid.NewGuid().ToString(), "Sub");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task ReorderHabits_Success_RoutesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var json = $"[{{\"habitId\":\"{Guid.NewGuid()}\",\"position\":0}}]";
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.ReorderHabits(_user, json));

        request.OperationId.Should().Be("reorder_habits");
        result.Should().Contain("Reordered 1 habits");
    }

    [Fact]
    public async Task ReorderHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Invalid positions");

        var json = "[]";
        var result = await _tools.ReorderHabits(_user, json);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task UpdateChecklist_Success_RoutesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var json = "[{\"text\":\"Item 1\",\"isChecked\":true}]";
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.UpdateChecklist(_user, Guid.NewGuid().ToString(), json));

        request.OperationId.Should().Be("update_checklist");
        result.Should().Contain("Updated checklist");
        result.Should().Contain("1 items");
    }

    [Fact]
    public async Task UpdateChecklist_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Habit not found");

        var json = "[]";
        var result = await _tools.UpdateChecklist(_user, Guid.NewGuid().ToString(), json);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task LinkGoalsToHabit_Success_RoutesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var goalId = Guid.NewGuid();
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.LinkGoalsToHabit(_user, Guid.NewGuid().ToString(), goalId.ToString()));

        request.OperationId.Should().Be("link_goals_to_habit");
        result.Should().Contain("Linked 1 goals");
    }

    [Fact]
    public async Task LinkGoalsToHabit_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Not found");

        var result = await _tools.LinkGoalsToHabit(_user, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkCreateHabits_Success_RoutesThroughExecutorAndFormatsBreakdown(bool partial)
    {
        var id = Guid.NewGuid();
        var items = new List<BulkCreateItemResult> { new(0, BulkItemStatus.Success, id, "Habit1") };
        if (partial)
            items.Add(new(1, BulkItemStatus.Failed, Title: "Habit2", Error: "Invalid schedule"));
        var bulkResult = new BulkCreateResult(items);
        StubExecutor(AgentOperationStatus.Succeeded, payload: bulkResult);

        var json = "[{\"title\":\"Habit1\",\"dueDate\":\"2026-04-01\"}]";
        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkCreateHabits(_user, json));

        request.OperationId.Should().Be("bulk_create_habits");
        result.Should().Contain($"1 succeeded, {(partial ? 1 : 0)} failed");
        result.Should().Contain("\"applied_count\":1");
        result.Should().Contain($"\"total_matched\":{items.Count}");
        result.Should().Contain($"\"partial\":{partial.ToString().ToLowerInvariant()}");
    }

    [Theory]
    [InlineData("bulk_update_habits")]
    [InlineData("bulk_reschedule_habits")]
    [InlineData("bulk_delete_habits")]
    [InlineData("bulk_log_habits")]
    [InlineData("bulk_skip_habits")]
    public async Task BulkMutations_ReturnStructuredCoverageEvenWithoutSummary(string operationId)
    {
        StubExecutor(AgentOperationStatus.Succeeded, payload: new
        {
            applied_count = 200,
            total_matched = 250,
            skipped_count = 50,
            partial = true
        });
        var id = Guid.NewGuid().ToString();
        var result = operationId switch
        {
            "bulk_update_habits" => await _tools.BulkUpdateHabits(_user, "{\"all\":true}", "{\"description\":\"Shared\"}"),
            "bulk_reschedule_habits" => await _tools.BulkRescheduleHabits(_user, "{\"all\":true}", "2026-04-02"),
            "bulk_delete_habits" => await _tools.BulkDeleteHabits(_user, id),
            "bulk_log_habits" => await _tools.BulkLogHabits(_user, id),
            _ => await _tools.BulkSkipHabits(_user, id)
        };

        result.Should().Contain("\"applied_count\":200");
        result.Should().Contain("\"total_matched\":250");
        result.Should().Contain("\"partial\":true");
        result.Should().NotContain("completed");
    }

    [Theory]
    [InlineData("infer")]
    [InlineData("explicit")]
    [InlineData("clear")]
    public async Task BulkUpdateHabitEmojis_ForwardsFilterAndConfirmationAndReportsCoverage(string mode)
    {
        StubExecutor(AgentOperationStatus.Succeeded, payload: new
        {
            applied_count = 225,
            total_matched = 225,
            skipped_count = 0,
            partial = false
        });
        var filter = "{\"tag\":\"Health\",\"include_completed\":true}";
        string? output = null;
        var request = await CapturedRequestAsync(async () => output = mode switch
        {
            "infer" => await _tools.BulkUpdateHabitEmojis(_user, filter, confirmationToken: "approved"),
            "explicit" => await _tools.BulkUpdateHabitEmojis(_user, filter, "💊", confirmationToken: "approved"),
            _ => await _tools.BulkUpdateHabitEmojis(_user, filter, null, confirmationToken: "approved")
        });

        request.OperationId.Should().Be("bulk_update_habit_emojis");
        request.ConfirmationToken.Should().Be("approved");
        request.Arguments.GetProperty("filter").GetProperty("tag").GetString().Should().Be("Health");
        request.Arguments.GetProperty("filter").GetProperty("include_completed").GetBoolean().Should().BeTrue();
        request.Arguments.GetProperty("infer_from_title").GetBoolean().Should().Be(mode == "infer");
        if (mode == "infer")
            request.Arguments.TryGetProperty("emoji", out _).Should().BeFalse();
        else if (mode == "clear")
            request.Arguments.GetProperty("emoji").ValueKind.Should().Be(JsonValueKind.Null);
        else
            request.Arguments.GetProperty("emoji").GetString().Should().Be("💊");
        output.Should().Contain("\"applied_count\":225");
        output.Should().Contain("\"total_matched\":225");
        output.Should().Contain("\"partial\":false");
    }

    [Theory]
    [InlineData(AgentOperationStatus.PendingConfirmation)]
    [InlineData(AgentOperationStatus.Denied)]
    public async Task BulkUpdateHabitEmojis_PreservesExecutorGate(AgentOperationStatus status)
    {
        StubExecutor(status, policyReason: "read_only_credential", pendingOperationId: Guid.NewGuid());

        var result = await _tools.BulkUpdateHabitEmojis(_user, "{\"all\":true}");

        result.Should().NotContain("applied_count");
        result.Should().Contain(status == AgentOperationStatus.PendingConfirmation ? "Confirmation required" : "read_only_credential");
    }

    [Fact]
    public async Task BulkCreateHabits_ReplayedPayload_PreservesFormattedResult()
    {
        var payload = new BulkCreateResult([new BulkCreateItemResult(0, BulkItemStatus.Success, Guid.NewGuid(), "Habit1")]);
        var original = new AgentExecuteOperationResponse(new AgentOperationResult("bulk_create_habits", "bulk_create_habits",
            AgentRiskClass.Destructive, AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.Succeeded, Payload: payload));
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var replay = System.Text.Json.JsonSerializer.Deserialize<AgentExecuteOperationResponse>(
            System.Text.Json.JsonSerializer.Serialize(original, options), options)!;
        _executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(original, replay);
        var first = await _tools.BulkCreateHabits(_user, """[{"title":"Habit1"}]""");
        var second = await _tools.BulkCreateHabits(_user, """[{"title":"Habit1"}]""");
        first.Should().Contain("1 succeeded, 0 failed").And.Contain("Habit1");
        second.Should().Be(first);
    }

    [Fact]
    public async Task BulkCreateHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Limit exceeded");

        var json = "[{\"title\":\"Habit1\",\"dueDate\":\"2026-04-01\"}]";
        var result = await _tools.BulkCreateHabits(_user, json);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkCreateHabits_WithEmoji_ForwardsParentAndChildEmojis()
    {
        StubExecutor(AgentOperationStatus.Succeeded);

        var request = await CapturedRequestAsync(async () =>
            await _tools.BulkCreateHabits(_user, """[{"title":"Workout","emoji":"🏋️","subHabits":[{"title":"Run","emoji":"🏃"}]}]"""));

        var habit = request.Arguments.GetProperty("habits")[0];
        habit.GetProperty("emoji").GetString().Should().Be("🏋️");
        habit.GetProperty("sub_habits")[0].GetProperty("emoji").GetString().Should().Be("🏃");
    }

    [Fact]
    public async Task BulkDeleteHabits_Success_RoutesThroughExecutor()
    {
        var id = Guid.NewGuid();
        var bulkResult = new BulkDeleteResult([
            new BulkDeleteItemResult(0, BulkItemStatus.Success, id)
        ]);
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Deleted 1 of 1 matching habit(s). Skipped 0. Complete result.", payload: bulkResult);

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkDeleteHabits(_user, id.ToString()));

        request.OperationId.Should().Be("bulk_delete_habits");
        result.Should().Contain("Deleted 1 of 1");
    }

    [Fact]
    public async Task BulkDeleteHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Error");

        var result = await _tools.BulkDeleteHabits(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkUpdateHabits_RoutesFilterAndUpdatesThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Updated 250 of 250 matching habit(s). Skipped 0. Complete result.");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkUpdateHabits(
            _user,
            """{"all":true}""",
            """{"emoji":"📚"}"""));

        request.OperationId.Should().Be("bulk_update_habits");
        request.Arguments.GetProperty("filter").GetProperty("all").GetBoolean().Should().BeTrue();
        request.Arguments.GetProperty("updates").GetProperty("emoji").GetString().Should().Be("📚");
        result.Should().Contain("250 of 250");
    }

    [Fact]
    public async Task BulkRescheduleHabits_RoutesFilterAndDateThroughExecutor()
    {
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Rescheduled 250 of 250 matching habit(s). Skipped 0. Complete result.");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkRescheduleHabits(
            _user,
            """{"all":true}""",
            "2026-09-12"));

        request.OperationId.Should().Be("bulk_reschedule_habits");
        request.Arguments.GetProperty("filter").GetProperty("all").GetBoolean().Should().BeTrue();
        request.Arguments.GetProperty("due_date").GetString().Should().Be("2026-09-12");
        result.Should().Contain("250 of 250");
    }

    [Fact]
    public async Task BulkLogHabits_Success_RoutesThroughExecutor()
    {
        var id = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Logged 1 of 1 matching habit(s). Skipped 0. Complete result.");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkLogHabits(_user, id.ToString()));

        request.OperationId.Should().Be("bulk_log_habits");
        result.Should().Contain("Logged 1 of 1");
    }

    [Fact]
    public async Task BulkLogHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Error");

        var result = await _tools.BulkLogHabits(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkSkipHabits_Success_RoutesThroughExecutor()
    {
        var id = Guid.NewGuid();
        StubExecutor(AgentOperationStatus.Succeeded, targetName: "Skipped 1 of 1 matching habit(s). Skipped 0. Complete result.");

        AgentExecuteOperationRequest request = null!;
        string result = string.Empty;
        request = await CapturedRequestAsync(async () => result = await _tools.BulkSkipHabits(_user, id.ToString()));

        request.OperationId.Should().Be("bulk_skip_habits");
        result.Should().Contain("Skipped 1 of 1");
    }

    [Fact]
    public async Task BulkSkipHabits_Failure_ReturnsError()
    {
        StubExecutor(AgentOperationStatus.Failed, policyReason: "Error");

        var result = await _tools.BulkSkipHabits(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetDailySummary_Success_ReturnsSummary()
    {
        var response = new DailySummaryResponse("You did great today!", "Knock out one more.", false);
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.GetDailySummary(_user, "2026-04-01", "2026-04-01");

        result.Should().Contain("You did great today!");
        result.Should().Contain("Summary");
    }

    [Fact]
    public async Task GetDailySummary_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DailySummaryResponse>("Pro required"));

        var result = await _tools.GetDailySummary(_user, "2026-04-01", "2026-04-01");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetRetrospective_Success_ReturnsRetrospective()
    {
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 4, 3));

        var response = new RetrospectiveResponse(
            "week",
            EmptyMetrics(),
            new RetrospectiveNarrative("Great week!", "", "", ""),
            FromCache: false);
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.GetRetrospective(_user, "week");

        result.Should().Contain("Great week!");
        result.Should().Contain("Retrospective (week)");
    }

    [Fact]
    public async Task GetRetrospective_Failure_ReturnsError()
    {
        _userDateService.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 4, 3));

        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<RetrospectiveResponse>("Pro required"));

        var result = await _tools.GetRetrospective(_user);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetHabitLogs_Success_ReturnsFormattedLogs()
    {
        var logs = new List<HabitLogResponse>
        {
            new(Guid.NewGuid(), new DateOnly(2026, 4, 1), 1, DateTime.UtcNow)
        };
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitLogResponse>>(logs));

        var result = await _tools.GetHabitLogs(_user, Guid.NewGuid().ToString());

        result.Should().Contain("2026-04-01");
    }

    [Fact]
    public async Task GetHabitLogs_Empty_ReturnsNoLogsMessage()
    {
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitLogResponse>>([]));

        var result = await _tools.GetHabitLogs(_user, Guid.NewGuid().ToString());

        result.Should().Contain("No logs found");
    }

    [Fact]
    public async Task GetHabitLogs_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<HabitLogResponse>>("Not found"));

        var result = await _tools.GetHabitLogs(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetAllHabitLogs_Success_ReturnsGroupedLogs()
    {
        var habitId = Guid.NewGuid();
        var dict = new Dictionary<Guid, List<HabitLogResponse>>
        {
            [habitId] = [new HabitLogResponse(Guid.NewGuid(), new DateOnly(2026, 4, 1), 1, DateTime.UtcNow)]
        };
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(dict));

        var result = await _tools.GetAllHabitLogs(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("1 habits");
        result.Should().Contain("partial: false");
    }

    [Fact]
    public async Task GetAllHabitLogs_WhenPerHabitOutputIsCapped_ReportsTruncation()
    {
        var habitId = Guid.NewGuid();
        var logs = Enumerable.Range(1, 12)
            .Select(day => new HabitLogResponse(Guid.NewGuid(), new DateOnly(2026, 4, day), 1, DateTime.UtcNow))
            .ToList();
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new Dictionary<Guid, List<HabitLogResponse>> { [habitId] = logs }));

        var result = await _tools.GetAllHabitLogs(_user, "2026-04-01", "2026-04-12");

        result.Should().Contain("total_logs: 12");
        result.Should().Contain("returned_logs: 10");
        result.Should().Contain("partial: true");
        result.Should().Contain("total 12, returned 10");
    }

    [Fact]
    public async Task GetAllHabitLogs_Empty_ReturnsNoLogsMessage()
    {
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new Dictionary<Guid, List<HabitLogResponse>>()));

        var result = await _tools.GetAllHabitLogs(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("No logs found");
    }
}
