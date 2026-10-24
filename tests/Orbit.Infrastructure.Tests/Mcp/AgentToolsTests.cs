using System.Security.Claims;
using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Orbit.Api.Mcp.Tools;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Mcp;

public class AgentToolsTests
{
    private readonly IAgentCatalogService _catalogService = Substitute.For<IAgentCatalogService>();
    private readonly IAgentOperationExecutor _operationExecutor = Substitute.For<IAgentOperationExecutor>();
    private readonly IPendingAgentOperationStore _pendingOperationStore = Substitute.For<IPendingAgentOperationStore>();
    private readonly IAgentStepUpService _stepUpService = Substitute.For<IAgentStepUpService>();
    private readonly AgentTools _tools;
    private static readonly Guid UserId = Guid.NewGuid();

    public AgentToolsTests()
    {
        _tools = new AgentTools(_catalogService, _operationExecutor, _pendingOperationStore, _stepUpService);
    }

    [Fact]
    public void ListMethods_ReturnCatalogValues()
    {
        var capability = new AgentCapability(
            AgentCapabilityIds.HabitsRead,
            "Habits",
            "Read habits",
            "habits",
            AgentScopes.ReadHabits,
            AgentRiskClass.Low,
            false,
            false,
            AgentConfirmationRequirement.None);
        var operation = new AgentOperation(
            "list_habits",
            "List habits",
            "Read habits",
            AgentCapabilityIds.HabitsRead,
            AgentRiskClass.Low,
            AgentConfirmationRequirement.None,
            false,
            true,
            Parse("{}"),
            Parse("{}"));
        var surface = new AppSurface("today", "Today", "Today view", [], [], [], []);
        var dataCatalog = new UserDataCatalogEntry("habits", "Habits", "Habit data", "low", "keep", true, true, []);

        _catalogService.GetCapabilities().Returns([capability]);
        _catalogService.GetOperations().Returns([operation]);
        _catalogService.GetSurfaces().Returns([surface]);
        _catalogService.GetUserDataCatalog().Returns([dataCatalog]);

        _tools.ListCapabilities().Should().ContainSingle().Which.Should().Be(capability);
        _tools.ListOperations().Should().ContainSingle().Which.Should().Be(operation);
        _tools.ListAppSurfaces().Should().ContainSingle().Which.Should().Be(surface);
        _tools.ListUserDataCatalog().Should().ContainSingle().Which.Should().Be(dataCatalog);
    }

    [Fact]
    public async Task ExecuteAgentOperation_UsesDefaultArgumentsAndUserContext()
    {
        var user = CreateUser();
        var response = new AgentExecuteOperationResponse(new AgentOperationResult(
            "list_habits",
            "list_habits",
            AgentRiskClass.Low,
            AgentConfirmationRequirement.None,
            AgentOperationStatus.Succeeded));
        _operationExecutor.ExecuteAsync(Arg.Any<AgentExecuteOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(response);

        var result = await _tools.ExecuteAgentOperation(user, "list_habits", null, "token-123", CancellationToken.None);

        result.Should().Be(response);
        await _operationExecutor.Received(1).ExecuteAsync(
            Arg.Is<AgentExecuteOperationRequest>(request =>
                request.UserId == UserId &&
                request.OperationId == "list_habits" &&
                request.Surface == AgentExecutionSurface.Mcp &&
                request.AuthMethod == AgentAuthMethod.Jwt &&
                request.ConfirmationToken == "token-123" &&
                request.Arguments.ValueKind == JsonValueKind.Object &&
                !request.Arguments.EnumerateObject().Any()),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("update_goal")]
    [InlineData("UPDATE_GOAL")]
    public async Task ExecuteAgentOperation_ExplicitNullDeadline_ClearsDeadlineThroughSharedTool(string operationId)
    {
        var goal = Goal.Create(new Goal.CreateGoalParams(
            UserId, "Read", 12, "books", "Keep description", new DateOnly(2027, 1, 1))).Value;
        var repository = Substitute.For<IGenericRepository<Goal>>();
        repository.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Goal, bool>>>(),
            Arg.Any<Func<IQueryable<Goal>, IQueryable<Goal>>?>(),
            Arg.Any<CancellationToken>()).Returns(goal);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var executor = SharedToolExecution.Create(new UpdateGoalTool(repository, Orbit.Tests.Shared.GoalToolCommandMediator.Create(repository, unitOfWork)), unitOfWork);
        var tools = new AgentTools(_catalogService, executor, _pendingOperationStore, _stepUpService);
        var arguments = JsonSerializer.SerializeToElement(new { goal_id = goal.Id, deadline = (string?)null });

        var result = await tools.ExecuteAgentOperation(CreateUser(), operationId, arguments);

        result.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        goal.Deadline.Should().BeNull();
        goal.Description.Should().Be("Keep description");
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("description")]
    [InlineData("emoji")]
    [InlineData("frequency_unit")]
    [InlineData("due_time")]
    [InlineData("due_end_time")]
    [InlineData("end_date")]
    public async Task ExecuteAgentOperation_ExplicitNullHabitField_ClearsOnlyThatField(string field)
    {
        var today = new DateOnly(2026, 4, 3);
        var dueTime = new TimeOnly(8, 0);
        DateOnly? endDate = field == "frequency_unit" ? null : today.AddDays(10);
        var habit = Habit.Create(new HabitCreateParams(HabitSchedule.Recurring,
            UserId, "Read", FrequencyUnit.Day, 1, today, Description: "Keep description",
            Emoji: "📚", DueTime: dueTime, DueEndTime: new TimeOnly(15, 0), EndDate: endDate)).Value;
        var repository = Substitute.For<IGenericRepository<Habit>>();
        repository.FindOneTrackedAsync(
            Arg.Any<Expression<Func<Habit, bool>>>(),
            Arg.Any<Func<IQueryable<Habit>, IQueryable<Habit>>?>(),
            Arg.Any<CancellationToken>()).Returns(habit);
        var userDateService = Substitute.For<IUserDateService>();
        userDateService.GetUserTodayAsync(UserId, Arg.Any<CancellationToken>()).Returns(today);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(
                Arg.Any<Func<CancellationToken, Task<Result<string>>>>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<string>>>>(0)(
                call.ArgAt<CancellationToken>(1)));
        var tool = SharedToolExecution.CreateHabitTool(repository, userDateService, unitOfWork);
        var executor = SharedToolExecution.Create(tool, unitOfWork);
        var tools = new AgentTools(_catalogService, executor, _pendingOperationStore, _stepUpService);
        var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["habit_id"] = habit.Id,
            [field] = null
        });

        var result = await tools.ExecuteAgentOperation(CreateUser(), "update_habit", arguments);

        result.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        habit.Title.Should().Be("Read");
        habit.Description.Should().Be(field == "description" ? null : "Keep description");
        habit.Emoji.Should().Be(field == "emoji" ? null : "📚");
        habit.FrequencyUnit.Should().Be(field == "frequency_unit" ? null : FrequencyUnit.Day);
        habit.DueTime.Should().Be(field == "due_time" ? null : dueTime);
        habit.EndDate.Should().Be(field == "end_date" ? null : endDate);
        habit.DueEndTime.Should().Be(field == "due_end_time" ? null : new TimeOnly(15, 0));
        await unitOfWork.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ConfirmAgentOperation_RefusesApiKeyCredentials()
    {
        var result = _tools.ConfirmAgentOperation(CreateUser(isApiKey: true), Guid.NewGuid().ToString());
        OAuthConnectorConfirmationTests.AssertError(result, "interactive_confirmation_required");
        _pendingOperationStore.DidNotReceiveWithAnyArgs().Confirm(default, default);
    }

    [Fact]
    public void ConfirmAgentOperation_RejectsInvalidGuid()
    {
        OAuthConnectorConfirmationTests.AssertError(
            _tools.ConfirmAgentOperation(CreateUser(), "bad-guid"), "invalid_pending_operation_id");
    }

    [Fact]
    public void ConfirmAgentOperation_ReturnsStoreResult()
    {
        var id = Guid.NewGuid();
        var confirmation = new PendingAgentOperationConfirmation(id, "agc_token", DateTime.UtcNow.AddMinutes(5));
        _pendingOperationStore.Confirm(UserId, id).Returns(confirmation);

        OAuthConnectorConfirmationTests.Payload<PendingAgentOperationConfirmation>(
            _tools.ConfirmAgentOperation(CreateUser(), id.ToString())).Should().Be(confirmation);
    }

    [Fact]
    public async Task StepUpAgentOperation_RefusesApiKeyCredentials()
    {
        OAuthConnectorConfirmationTests.AssertError(
            await _tools.StepUpAgentOperation(CreateUser(isApiKey: true), Guid.NewGuid().ToString()),
            "interactive_confirmation_required");
        await _stepUpService.DidNotReceiveWithAnyArgs().IssueChallengeAsync(default, default, "", default);
    }

    [Fact]
    public async Task StepUpAgentOperation_RejectsInvalidGuid()
    {
        OAuthConnectorConfirmationTests.AssertError(
            await _tools.StepUpAgentOperation(CreateUser(), "bad-guid"), "invalid_pending_operation_id");
    }

    [Fact]
    public async Task StepUpAgentOperation_ReturnsChallenge()
    {
        var id = Guid.NewGuid();
        var challenge = new AgentStepUpChallenge(Guid.NewGuid(), id, DateTime.UtcNow.AddMinutes(10));
        _stepUpService.IssueChallengeAsync(UserId, id, "pt-BR", Arg.Any<CancellationToken>())
            .Returns(Result.Success(challenge));

        OAuthConnectorConfirmationTests.Payload<AgentStepUpChallenge>(
            await _tools.StepUpAgentOperation(CreateUser(), id.ToString(), "pt-BR")).Should().Be(challenge);
    }

    [Fact]
    public async Task StepUpAgentOperation_ReportsChallengeFailure()
    {
        var id = Guid.NewGuid();
        _stepUpService.IssueChallengeAsync(UserId, id, "en", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AgentStepUpChallenge>("issue_failed"));

        OAuthConnectorConfirmationTests.AssertError(
            await _tools.StepUpAgentOperation(CreateUser(), id.ToString()), "step_up_failed");
    }

    [Fact]
    public async Task VerifyStepUpAgentOperation_RefusesApiKeyCredentials()
    {
        OAuthConnectorConfirmationTests.AssertError(
            await _tools.VerifyStepUpAgentOperation(CreateUser(isApiKey: true),
                Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "123456"), "interactive_confirmation_required");
        await _stepUpService.DidNotReceiveWithAnyArgs().VerifyChallengeAsync(default, default, default, "", default);
    }

    [Theory]
    [InlineData("bad-guid", "5078c455-de14-4cc7-8044-42ca434cddf0", "invalid_pending_operation_id")]
    [InlineData("5078c455-de14-4cc7-8044-42ca434cddf0", "bad-guid", "invalid_challenge_id")]
    public async Task VerifyStepUpAgentOperation_RejectsInvalidIds(string pendingId, string challengeId, string code)
    {
        OAuthConnectorConfirmationTests.AssertError(
            await _tools.VerifyStepUpAgentOperation(CreateUser(), pendingId, challengeId, "123456"), code);
    }

    [Fact]
    public async Task VerifyStepUpAgentOperation_ReturnsPendingOperation()
    {
        var id = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        var pending = new PendingAgentOperation(id, AgentCapabilityIds.HabitsDelete, "Delete habit", "Delete a habit",
            AgentRiskClass.High, AgentConfirmationRequirement.StepUp, DateTime.UtcNow.AddMinutes(10));
        _stepUpService.VerifyChallengeAsync(UserId, id, challengeId, "123456", Arg.Any<CancellationToken>())
            .Returns(Result.Success(pending));

        OAuthConnectorConfirmationTests.Payload<PendingAgentOperation>(await _tools.VerifyStepUpAgentOperation(
            CreateUser(), id.ToString(), challengeId.ToString(), "123456")).Should().Be(pending);
    }

    [Fact]
    public async Task VerifyStepUpAgentOperation_ReportsVerificationFailure()
    {
        var id = Guid.NewGuid();
        var challengeId = Guid.NewGuid();
        _stepUpService.VerifyChallengeAsync(UserId, id, challengeId, "123456", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<PendingAgentOperation>("verify_failed"));

        OAuthConnectorConfirmationTests.AssertError(await _tools.VerifyStepUpAgentOperation(
            CreateUser(), id.ToString(), challengeId.ToString(), "123456"), "step_up_failed");
    }

    private static ClaimsPrincipal CreateUser(bool isApiKey = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, UserId.ToString()),
            new("scope", AgentScopes.ReadHabits),
            new("scope", AgentScopes.WriteHabits)
        };

        if (isApiKey)
            claims.Add(new Claim("auth_method", "api_key"));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}
