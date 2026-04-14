using System.Security.Claims;
using FluentAssertions;
using MediatR;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Domain.ValueObjects;

namespace Orbit.Infrastructure.Tests.Mcp;

public class HabitToolsTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly HabitTools _tools;
    private readonly ClaimsPrincipal _user;

    public HabitToolsTests()
    {
        _tools = new HabitTools(_mediator, _userDateService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        _user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task ListHabits_Success_ReturnsFormattedList()
    {
        var items = new List<HabitScheduleItem>
        {
            new(Guid.NewGuid(), "Read", null, null, FrequencyUnit.Day, 1, false, false, false, false,
                [], null, DateTime.UtcNow, DateOnly.FromDateTime(DateTime.UtcNow), null, null, null,
                [], false, false, [], [], false, [], [], [], [], false, null, null, [])
        };
        var paginated = new PaginatedResponse<HabitScheduleItem>(items, 1, 50, 1, 1);
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(paginated));

        var result = await _tools.ListHabits(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("Read");
        result.Should().Contain("page 1/1");
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
            habitId, "Exercise", "Go for a run", null, FrequencyUnit.Day, 1,
            false, false, false, false,
            DateOnly.FromDateTime(DateTime.UtcNow), null, null, null,
            [], null, false, [], [], [], DateTime.UtcNow, []);

        _mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(detail));

        var result = await _tools.GetHabit(_user, habitId.ToString());

        result.Should().Contain("Exercise");
        result.Should().Contain("Active");
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
    public async Task CreateHabit_Success_ReturnsCreatedMessage()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var result = await _tools.CreateHabit(_user, "New Habit", "2026-04-01");

        result.Should().Contain("Created habit 'New Habit'");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task CreateHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Title is required"));

        var result = await _tools.CreateHabit(_user, "", "2026-04-01");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task UpdateHabit_Success_ReturnsUpdatedMessage()
    {
        var habitId = Guid.NewGuid();
        _mediator.Send(Arg.Any<UpdateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.UpdateHabit(_user, habitId.ToString(), "Updated Title");

        result.Should().Contain("Updated habit");
    }

    [Fact]
    public async Task UpdateHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<UpdateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit not found"));

        var result = await _tools.UpdateHabit(_user, Guid.NewGuid().ToString(), "Title");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DeleteHabit_Success_ReturnsDeletedMessage()
    {
        var habitId = Guid.NewGuid();
        _mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.DeleteHabit(_user, habitId.ToString());

        result.Should().Contain("Deleted habit");
    }

    [Fact]
    public async Task DeleteHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit not found"));

        var result = await _tools.DeleteHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task LogHabit_Success_ReturnsLoggedMessage()
    {
        var logId = Guid.NewGuid();
        var response = new LogHabitResponse(logId, true, 5);
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(response));

        var result = await _tools.LogHabit(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Logged habit");
    }

    [Fact]
    public async Task LogHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LogHabitResponse>("Habit not found"));

        var result = await _tools.LogHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task SkipHabit_Success_ReturnsSkippedMessage()
    {
        _mediator.Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.SkipHabit(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Skipped habit");
    }

    [Fact]
    public async Task SkipHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not a recurring habit"));

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
    public async Task GetHabitMetrics_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitMetrics>("Habit not found"));

        var result = await _tools.GetHabitMetrics(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task DuplicateHabit_Success_ReturnsDuplicatedMessage()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<DuplicateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var habitId = Guid.NewGuid();
        var result = await _tools.DuplicateHabit(_user, habitId.ToString());

        result.Should().Contain("Duplicated habit");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task DuplicateHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<DuplicateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Habit not found"));

        var result = await _tools.DuplicateHabit(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task MoveHabitParent_Success_WithParent_ReturnsMovedMessage()
    {
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var habitId = Guid.NewGuid();
        var parentId = Guid.NewGuid();
        var result = await _tools.MoveHabitParent(_user, habitId.ToString(), parentId.ToString());

        result.Should().Contain("Moved habit");
        result.Should().Contain("under parent");
    }

    [Fact]
    public async Task MoveHabitParent_Success_NoParent_ReturnsPromotedMessage()
    {
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var result = await _tools.MoveHabitParent(_user, Guid.NewGuid().ToString());

        result.Should().Contain("Promoted habit");
        result.Should().Contain("top-level");
    }

    [Fact]
    public async Task MoveHabitParent_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit not found"));

        var result = await _tools.MoveHabitParent(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task CreateSubHabit_Success_ReturnsCreatedMessage()
    {
        var newId = Guid.NewGuid();
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(newId));

        var parentId = Guid.NewGuid();
        var result = await _tools.CreateSubHabit(_user, parentId.ToString(), "Sub Habit");

        result.Should().Contain("Created sub-habit 'Sub Habit'");
        result.Should().Contain(newId.ToString());
    }

    [Fact]
    public async Task CreateSubHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Parent not found"));

        var result = await _tools.CreateSubHabit(_user, Guid.NewGuid().ToString(), "Sub");

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task ReorderHabits_Success_ReturnsReorderedMessage()
    {
        _mediator.Send(Arg.Any<ReorderHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var json = $"[{{\"habitId\":\"{Guid.NewGuid()}\",\"position\":0}}]";
        var result = await _tools.ReorderHabits(_user, json);

        result.Should().Contain("Reordered 1 habits");
    }

    [Fact]
    public async Task ReorderHabits_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<ReorderHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Invalid positions"));

        var json = "[]";
        var result = await _tools.ReorderHabits(_user, json);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkLogHabits_Success_ReturnsBulkLogMessage()
    {
        var id = Guid.NewGuid();
        var bulkResult = new BulkLogResult([new BulkLogItemResult(0, BulkItemStatus.Success, id, Guid.NewGuid())]);
        _mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(bulkResult));

        var result = await _tools.BulkLogHabits(_user, id.ToString());

        result.Should().Contain("Bulk log: 1/1 logged successfully");
    }

    [Fact]
    public async Task BulkLogHabits_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkLogResult>("Error"));

        var result = await _tools.BulkLogHabits(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkSkipHabits_Success_ReturnsBulkSkipMessage()
    {
        var id = Guid.NewGuid();
        var bulkResult = new BulkSkipResult([new BulkSkipItemResult(0, BulkItemStatus.Success, id)]);
        _mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(bulkResult));

        var result = await _tools.BulkSkipHabits(_user, id.ToString());

        result.Should().Contain("Bulk skip: 1/1 skipped successfully");
    }

    [Fact]
    public async Task BulkSkipHabits_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkSkipResult>("Error"));

        var result = await _tools.BulkSkipHabits(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkCreateHabits_Success_ReturnsBulkCreateMessage()
    {
        var id = Guid.NewGuid();
        var bulkResult = new BulkCreateResult([
            new BulkCreateItemResult(0, BulkItemStatus.Success, id, "Habit1")
        ]);
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(bulkResult));

        var json = "[{\"title\":\"Habit1\",\"dueDate\":\"2026-04-01\"}]";
        var result = await _tools.BulkCreateHabits(_user, json);

        result.Should().Contain("1 succeeded, 0 failed");
    }

    [Fact]
    public async Task BulkCreateHabits_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkCreateResult>("Limit exceeded"));

        var json = "[]";
        var result = await _tools.BulkCreateHabits(_user, json);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task BulkDeleteHabits_Success_ReturnsBulkDeleteMessage()
    {
        var id = Guid.NewGuid();
        var bulkResult = new BulkDeleteResult([
            new BulkDeleteItemResult(0, BulkItemStatus.Success, id)
        ]);
        _mediator.Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(bulkResult));

        var result = await _tools.BulkDeleteHabits(_user, id.ToString());

        result.Should().Contain("1/1 deleted successfully");
    }

    [Fact]
    public async Task BulkDeleteHabits_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkDeleteResult>("Error"));

        var result = await _tools.BulkDeleteHabits(_user, Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task GetDailySummary_Success_ReturnsSummary()
    {
        var response = new DailySummaryResponse("You did great today!", false);
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

        var response = new RetrospectiveResponse("Great week!", false);
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
            new(Guid.NewGuid(), new DateOnly(2026, 4, 1), 1, "Done", DateTime.UtcNow)
        };
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitLogResponse>>(logs));

        var result = await _tools.GetHabitLogs(_user, Guid.NewGuid().ToString());

        result.Should().Contain("2026-04-01");
        result.Should().Contain("Done");
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
            [habitId] = [new HabitLogResponse(Guid.NewGuid(), new DateOnly(2026, 4, 1), 1, null, DateTime.UtcNow)]
        };
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(dict));

        var result = await _tools.GetAllHabitLogs(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("1 habits");
    }

    [Fact]
    public async Task GetAllHabitLogs_Empty_ReturnsNoLogsMessage()
    {
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new Dictionary<Guid, List<HabitLogResponse>>()));

        var result = await _tools.GetAllHabitLogs(_user, "2026-04-01", "2026-04-07");

        result.Should().Contain("No logs found");
    }

    [Fact]
    public async Task UpdateChecklist_Success_ReturnsUpdatedMessage()
    {
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var json = "[{\"text\":\"Item 1\",\"isChecked\":true}]";
        var result = await _tools.UpdateChecklist(_user, Guid.NewGuid().ToString(), json);

        result.Should().Contain("Updated checklist");
        result.Should().Contain("1 items");
    }

    [Fact]
    public async Task UpdateChecklist_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Habit not found"));

        var json = "[]";
        var result = await _tools.UpdateChecklist(_user, Guid.NewGuid().ToString(), json);

        result.Should().StartWith("Error: ");
    }

    [Fact]
    public async Task LinkGoalsToHabit_Success_ReturnsLinkedMessage()
    {
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        var goalId = Guid.NewGuid();
        var result = await _tools.LinkGoalsToHabit(_user, Guid.NewGuid().ToString(), goalId.ToString());

        result.Should().Contain("Linked 1 goals");
    }

    [Fact]
    public async Task LinkGoalsToHabit_Failure_ReturnsError()
    {
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure("Not found"));

        var result = await _tools.LinkGoalsToHabit(_user, Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        result.Should().StartWith("Error: ");
    }
}
