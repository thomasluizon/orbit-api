using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Api.Mcp;
using Orbit.Api.Mcp.Tools;
using Orbit.Api.Middleware;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Mcp;

public sealed class GeneralHabitSkipTests : IDisposable
{
    private static readonly DateOnly Today = new(2026, 4, 3);
    private readonly Guid _userId = Guid.NewGuid();
    private readonly List<Habit> _habits = [];
    private readonly IGenericRepository<Habit> _repository = Substitute.For<IGenericRepository<Habit>>();
    private readonly IGenericRepository<HabitLog> _logs = Substitute.For<IGenericRepository<HabitLog>>();
    private readonly IGenericRepository<HabitSkipUndo> _receipts = Substitute.For<IGenericRepository<HabitSkipUndo>>();
    private readonly IUnitOfWork _unit = Substitute.For<IUnitOfWork>();
    private readonly IUserDateService _dates = Substitute.For<IUserDateService>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly SkipHabitTool _single;
    private readonly BulkSkipHabitsTool _bulk;
    private readonly HabitTools _mcp;
    private readonly ClaimsPrincipal _user;
    private readonly List<AgentOperationResult> _outcomes = [];

    public GeneralHabitSkipTests()
    {
        _user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, _userId.ToString())], "Test"));
        _dates.GetUserTodayAsync(_userId, Arg.Any<CancellationToken>()).Returns(Today);
        _repository.TryRefreshAsync(Arg.Any<Habit>(), Arg.Any<CancellationToken>()).Returns(true);
        _repository.FindOneTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => _habits.SingleOrDefault(call.Arg<Expression<Func<Habit, bool>>>().Compile()));
        _repository.FindTrackedAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => _habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _repository.FindAsync(Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => _habits.Where(call.Arg<Expression<Func<Habit, bool>>>().Compile()).ToList());
        _unit.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<SkipHabitResponse>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<SkipHabitResponse>>>>()(call.Arg<CancellationToken>()));
        _unit.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<bool>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<bool>>>()(call.Arg<CancellationToken>()));
        var singleHandler = new SkipHabitCommandHandler(new SkipHabitRepositories(_repository, _logs, _receipts),
            _dates, Substitute.For<IGoalCompletionService>(), _unit, _cache);
        var bulkHandler = new BulkSkipHabitsCommandHandler(_repository, _logs, _dates, _unit, _cache);
        _mediator.Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => singleHandler.Handle(call.Arg<SkipHabitCommand>(), call.Arg<CancellationToken>()));
        _mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call => bulkHandler.Handle(call.Arg<BulkSkipHabitsCommand>(), call.Arg<CancellationToken>()));
        _single = new SkipHabitTool(_mediator, _repository);
        _bulk = new BulkSkipHabitsTool(_mediator, _repository, _dates,
            new BulkHabitReplayPlanner(Substitute.For<IIdempotencyContext>(), Substitute.For<IIdempotencyStore>(), _unit));
        var executor = Substitute.For<IAgentOperationExecutor>();
        executor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var request = call.Arg<AgentExecuteOperationRequest>();
            IAiTool tool = request.OperationId == _single.Name ? _single : _bulk;
            var result = await tool.ExecuteAsync(request.Arguments, request.UserId, call.Arg<CancellationToken>());
            var response = AgentOperationResponseFactory.ToolOutcome(tool.Name, true, AgentRiskClass.Low,
                AgentConfirmationRequirement.None, tool.Name, result,
                result.Success ? AgentOperationStatus.Succeeded : AgentOperationStatus.Failed, false);
            _outcomes.Add(response.Operation);
            return response;
        });
        _mcp = new HabitTools(_mediator, _dates, new McpExecutorBridge(executor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectApi_GeneralSkip_ReturnsTypedRejectionWithoutWrites(bool bulk)
    {
        var habit = AddHabit(HabitSchedule.General);
        var before = JsonSerializer.Serialize(habit);
        var controller = new HabitsController(_mediator, NullLogger<HabitsController>.Instance, _dates)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = _user } }
        };

        var response = bulk
            ? await controller.BulkSkip(new HabitsController.BulkSkipHabitsRequest([new(habit.Id)]), default)
            : await controller.SkipHabit(habit.Id, null, default);

        var body = response.Should().BeAssignableTo<ObjectResult>().Subject;
        if (bulk)
            body.Value.Should().BeOfType<BulkSkipResult>().Subject.Results.Should().ContainSingle()
                .Which.ErrorCode.Should().Be(DomainErrors.CannotSkipGeneralHabit.Code);
        else
        {
            body.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
            body.Value.Should().BeOfType<ErrorResponse>().Subject.ErrorCode.Should().Be(DomainErrors.CannotSkipGeneralHabit.Code);
        }
        await AssertUnchanged(habit, before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mcp_GeneralSkip_ExposesTheSameToolRejection(bool bulk)
    {
        var habit = AddHabit(HabitSchedule.General);
        var before = JsonSerializer.Serialize(habit);

        var output = bulk
            ? await _mcp.BulkSkipHabits(_user, habit.Id.ToString())
            : await _mcp.SkipHabit(_user, habit.Id.ToString());

        output.Should().Be($"Error: {DomainErrors.CannotSkipGeneralHabit.Message}");
        _outcomes.Should().ContainSingle().Which.ErrorCode.Should().Be(DomainErrors.CannotSkipGeneralHabit.Code);
        await AssertUnchanged(habit, before);
    }

    [Fact]
    public async Task Mcp_MixedBulk_ReportsGeneralRejectionAndDatedSuccessCounts()
    {
        var general = AddHabit(HabitSchedule.General);
        var oneTime = AddHabit(HabitSchedule.OneTime);
        var recurring = AddHabit(HabitSchedule.Recurring);
        var before = JsonSerializer.Serialize(general);

        var output = await _mcp.BulkSkipHabits(_user, string.Join(',', _habits.Select(habit => habit.Id)));

        output.Should().Contain("Skipped 2 of 3").And.Contain(DomainErrors.CannotSkipGeneralHabit.Code);
        oneTime.NextDueDate.Should().Be(Today.AddDays(1));
        recurring.NextDueDate.Should().BeAfter(Today);
        JsonSerializer.Serialize(general).Should().Be(before);
        await _unit.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private Habit AddHabit(HabitSchedule schedule)
    {
        var habit = Habit.Create(new HabitCreateParams(schedule, _userId, "Read",
            schedule == HabitSchedule.Recurring ? FrequencyUnit.Day : null,
            schedule == HabitSchedule.Recurring ? 1 : null, Today)).Value;
        _habits.Add(habit);
        return habit;
    }

    private async Task AssertUnchanged(Habit habit, string before)
    {
        JsonSerializer.Serialize(habit).Should().Be(before);
        await _logs.DidNotReceive().AddAsync(Arg.Any<HabitLog>(), Arg.Any<CancellationToken>());
        await _receipts.DidNotReceive().AddAsync(Arg.Any<HabitSkipUndo>(), Arg.Any<CancellationToken>());
        await _unit.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    public void Dispose() => _cache.Dispose();
}
