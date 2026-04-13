using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Api.Controllers;
using Orbit.Application.Common;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Queries;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Controllers;

public class HabitsControllerTests
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ILogger<HabitsController> _logger = Substitute.For<ILogger<HabitsController>>();
    private readonly IUserDateService _userDateService = Substitute.For<IUserDateService>();
    private readonly HabitsController _controller;
    private static readonly Guid UserId = Guid.NewGuid();

    public HabitsControllerTests()
    {
        _controller = new HabitsController(_mediator, _logger, _userDateService);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
    }

    [Fact]
    public async Task GetHabits_Success_ReturnsOk()
    {
        var filter = new HabitsController.GetHabitsFilterRequest { DateFrom = DateOnly.FromDateTime(DateTime.UtcNow), DateTo = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)) };
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(PaginatedResponse<HabitScheduleItem>)!));
        var result = await _controller.GetHabits(filter);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetHabits_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetHabitScheduleQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PaginatedResponse<HabitScheduleItem>>("Invalid date range"));
        var result = await _controller.GetHabits(new HabitsController.GetHabitsFilterRequest());
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetCalendarMonth_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetCalendarMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(CalendarMonthResponse)!));
        var result = await _controller.GetCalendarMonth(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetCalendarMonth_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetCalendarMonthQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<CalendarMonthResponse>("Error"));
        var result = await _controller.GetCalendarMonth(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetDailySummary_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(DailySummaryResponse)!));
        var result = await _controller.GetDailySummary(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)));
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetDailySummary_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<DailySummaryResponse>("Pro required"));
        var result = await _controller.GetDailySummary(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)));
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task GetDailySummary_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetDailySummaryQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<DailySummaryResponse>("Error"));
        var result = await _controller.GetDailySummary(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)));
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetRetrospective_Success_ReturnsOk()
    {
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(DateOnly.FromDateTime(DateTime.UtcNow));
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(RetrospectiveResponse)!));
        var result = await _controller.GetRetrospective("week");
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetRetrospective_PayGateFailure_Returns403()
    {
        _userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(DateOnly.FromDateTime(DateTime.UtcNow));
        _mediator.Send(Arg.Any<GetRetrospectiveQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<RetrospectiveResponse>("Pro required"));
        var result = await _controller.GetRetrospective("week");
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task GetHabitById_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(HabitDetailResponse)!));
        var result = await _controller.GetHabitById(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetHabitById_NotFound_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<GetHabitByIdQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitDetailResponse>("Habit not found"));
        var result = await _controller.GetHabitById(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task GetHabitDetail_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetHabitFullDetailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(HabitFullDetailResponse)!));
        var result = await _controller.GetHabitDetail(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetHabitDetail_NotFound_ReturnsNotFound()
    {
        _mediator.Send(Arg.Any<GetHabitFullDetailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitFullDetailResponse>("Habit not found"));
        var result = await _controller.GetHabitDetail(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task CreateHabit_Success_ReturnsCreated()
    {
        _mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(Guid.NewGuid()));
        var result = await _controller.CreateHabit(new HabitsController.CreateHabitRequest("Test", null, null, null), CancellationToken.None);
        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task CreateHabit_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<Guid>("Pro required"));
        var result = await _controller.CreateHabit(new HabitsController.CreateHabitRequest("Test", null, null, null), CancellationToken.None);
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task CreateHabit_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<CreateHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>("Validation failed"));
        var result = await _controller.CreateHabit(new HabitsController.CreateHabitRequest("Test", null, null, null), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task LogHabit_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(LogHabitResponse)!));
        var result = await _controller.LogHabit(Guid.NewGuid(), null, CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task LogHabit_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<LogHabitCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<LogHabitResponse>("Habit not found"));
        var result = await _controller.LogHabit(Guid.NewGuid(), null, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task SkipHabit_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await _controller.SkipHabit(Guid.NewGuid(), null, CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task SkipHabit_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<SkipHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure("Error"));
        var result = await _controller.SkipHabit(Guid.NewGuid(), null, CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateHabit_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<UpdateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await _controller.UpdateHabit(Guid.NewGuid(), new HabitsController.UpdateHabitRequest("Updated", null, null, null), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task UpdateHabit_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<UpdateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure("Error"));
        var result = await _controller.UpdateHabit(Guid.NewGuid(), new HabitsController.UpdateHabitRequest("Updated", null, null, null), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateChecklist_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await _controller.UpdateChecklist(Guid.NewGuid(), new HabitsController.UpdateChecklistRequest([]), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task UpdateChecklist_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<UpdateChecklistCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure("Error"));
        var result = await _controller.UpdateChecklist(Guid.NewGuid(), new HabitsController.UpdateChecklistRequest([]), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DeleteHabit_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await _controller.DeleteHabit(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task DeleteHabit_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DeleteHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure("Error"));
        var result = await _controller.DeleteHabit(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetAllLogs_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new Dictionary<Guid, List<HabitLogResponse>>()));
        var result = await _controller.GetAllLogs(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetAllLogs_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetAllHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Dictionary<Guid, List<HabitLogResponse>>>("Error"));
        var result = await _controller.GetAllLogs(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7)), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetLogs_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success<IReadOnlyList<HabitLogResponse>>([]));
        var result = await _controller.GetLogs(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetLogs_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetHabitLogsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<IReadOnlyList<HabitLogResponse>>("Error"));
        var result = await _controller.GetLogs(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetMetrics_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(HabitMetrics)!));
        var result = await _controller.GetMetrics(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task GetMetrics_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<GetHabitMetricsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<HabitMetrics>("Error"));
        var result = await _controller.GetMetrics(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BulkCreate_Success_Returns201()
    {
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(BulkCreateResult)!));
        var result = await _controller.BulkCreate(new HabitsController.BulkCreateHabitsRequest([]), CancellationToken.None);
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task BulkCreate_PassesSyncReviewMetadataToCommand()
    {
        BulkCreateHabitsCommand? captured = null;
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<BulkCreateHabitsCommand>();
                return Result.Success(new BulkCreateResult([]));
            });

        var request = new HabitsController.BulkCreateHabitsRequest(
            [
                new HabitsController.BulkHabitItemRequest(
                    "Imported Event",
                    null,
                    null,
                    null,
                    DueDate: DateOnly.FromDateTime(DateTime.UtcNow),
                    GoogleEventId: "evt_sync")
            ],
            FromSyncReview: true);

        var result = await _controller.BulkCreate(request, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>();
        captured.Should().NotBeNull();
        captured!.FromSyncReview.Should().BeTrue();
        captured.Habits.Should().ContainSingle();
        captured.Habits[0].GoogleEventId.Should().Be("evt_sync");
    }

    [Fact]
    public async Task BulkCreate_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.PayGateFailure<BulkCreateResult>("Pro required"));
        var result = await _controller.BulkCreate(new HabitsController.BulkCreateHabitsRequest([]), CancellationToken.None);
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task BulkCreate_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<BulkCreateHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkCreateResult>("Error"));
        var result = await _controller.BulkCreate(new HabitsController.BulkCreateHabitsRequest([]), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BulkDelete_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(BulkDeleteResult)!));
        var result = await _controller.BulkDelete(new HabitsController.BulkDeleteHabitsRequest([Guid.NewGuid()]), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task BulkDelete_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<BulkDeleteHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkDeleteResult>("Error"));
        var result = await _controller.BulkDelete(new HabitsController.BulkDeleteHabitsRequest([Guid.NewGuid()]), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BulkLog_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(BulkLogResult)!));
        var result = await _controller.BulkLog(new HabitsController.BulkLogHabitsRequest([]), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task BulkLog_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<BulkLogHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkLogResult>("Error"));
        var result = await _controller.BulkLog(new HabitsController.BulkLogHabitsRequest([]), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BulkSkip_Success_ReturnsOk()
    {
        _mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(default(BulkSkipResult)!));
        var result = await _controller.BulkSkip(new HabitsController.BulkSkipHabitsRequest([]), CancellationToken.None);
        result.Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task BulkSkip_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<BulkSkipHabitsCommand>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<BulkSkipResult>("Error"));
        var result = await _controller.BulkSkip(new HabitsController.BulkSkipHabitsRequest([]), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ReorderHabits_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<ReorderHabitsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await _controller.ReorderHabits(new HabitsController.ReorderHabitsRequest([]), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task ReorderHabits_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<ReorderHabitsCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure("Error"));
        var result = await _controller.ReorderHabits(new HabitsController.ReorderHabitsRequest([]), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task MoveHabitParent_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await _controller.MoveHabitParent(Guid.NewGuid(), new HabitsController.MoveHabitParentRequest(null), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task MoveHabitParent_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<MoveHabitParentCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure("Error"));
        var result = await _controller.MoveHabitParent(Guid.NewGuid(), new HabitsController.MoveHabitParentRequest(null), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task DuplicateHabit_Success_ReturnsCreated()
    {
        _mediator.Send(Arg.Any<DuplicateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(Guid.NewGuid()));
        var result = await _controller.DuplicateHabit(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task DuplicateHabit_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<DuplicateHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure<Guid>("Error"));
        var result = await _controller.DuplicateHabit(Guid.NewGuid(), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateSubHabit_Success_ReturnsCreated()
    {
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success(Guid.NewGuid()));
        var result = await _controller.CreateSubHabit(Guid.NewGuid(), new HabitsController.CreateSubHabitRequest("Sub"), CancellationToken.None);
        result.Should().BeOfType<CreatedAtActionResult>();
    }

    [Fact]
    public async Task CreateSubHabit_PayGateFailure_Returns403()
    {
        _mediator.Send(Arg.Any<CreateSubHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.PayGateFailure<Guid>("Pro required"));
        var result = await _controller.CreateSubHabit(Guid.NewGuid(), new HabitsController.CreateSubHabitRequest("Sub"), CancellationToken.None);
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(403);
    }

    [Fact]
    public async Task LinkGoals_Success_ReturnsNoContent()
    {
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var result = await _controller.LinkGoals(Guid.NewGuid(), new HabitsController.LinkGoalsRequest([Guid.NewGuid()]), CancellationToken.None);
        result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task LinkGoals_Failure_ReturnsBadRequest()
    {
        _mediator.Send(Arg.Any<LinkGoalsToHabitCommand>(), Arg.Any<CancellationToken>()).Returns(Result.Failure("Error"));
        var result = await _controller.LinkGoals(Guid.NewGuid(), new HabitsController.LinkGoalsRequest([Guid.NewGuid()]), CancellationToken.None);
        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
