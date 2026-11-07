using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class PlanCommittedOutcomeTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task ApprovedCreate_ConversationPageCarriesTheConfirmResponseOperation()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();

        var response = await ConfirmAndExecuteAsync(fixture, id);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded);
        fixture.Db.ChangeTracker.Clear();
        var goal = await fixture.Db.Goals.SingleAsync();
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("approved");
        var execution = outcome.GetProperty("Execution");
        execution.GetProperty("status").GetString().Should().Be("Succeeded");
        execution.GetProperty("targetId").GetString().Should().Be(goal.Id.ToString());
        JsonNode.DeepEquals(JsonNode.Parse(execution.GetRawText()), JsonSerializer.SerializeToNode(response.Operation, WireOptions))
            .Should().BeTrue("the stored execution must equal the operation object of the live confirm response");
    }

    [Fact]
    public async Task ApprovedUpdate_ConversationPageCarriesTheChangedTarget()
    {
        using var fixture = new Fixture();
        var goal = Goal.Create(new Goal.CreateGoalParams(fixture.UserId, "Read books", 10, "books")).Value;
        fixture.Db.Goals.Add(goal);
        await fixture.Db.SaveChangesAsync();
        var id = await fixture.ProposeAsync(operationId: "update_goal",
            toolArguments: new { goal_id = goal.Id.ToString(), title = "Read more books" });

        var response = await ConfirmAndExecuteAsync(fixture, id);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, JsonSerializer.Serialize(response));
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Goals.SingleAsync()).Title.Should().Be("Read more books");
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("approved");
        var execution = outcome.GetProperty("Execution");
        execution.GetProperty("sourceName").GetString().Should().Be("update_goal");
        execution.GetProperty("status").GetString().Should().Be("Succeeded");
        execution.GetProperty("targetId").GetString().Should().Be(goal.Id.ToString());
        execution.GetProperty("targetName").GetString().Should().Be("Read more books");
    }

    [Fact]
    public async Task FailedExecution_ConversationPageCarriesTheFailedStatus()
    {
        using var fixture = new Fixture(failGoal: true);
        var id = await fixture.ProposeAsync();

        var response = await ConfirmAndExecuteAsync(fixture, id);

        response.Operation.Status.Should().Be(AgentOperationStatus.Failed);
        fixture.Db.ChangeTracker.Clear();
        fixture.Db.Goals.Should().BeEmpty();
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("approved");
        var execution = outcome.GetProperty("Execution");
        execution.GetProperty("status").GetString().Should().Be("Failed");
        execution.GetProperty("sourceName").GetString().Should().Be("create_goal");
        JsonNode.DeepEquals(JsonNode.Parse(execution.GetRawText()), JsonSerializer.SerializeToNode(response.Operation, WireOptions))
            .Should().BeTrue();
    }

    [Fact]
    public async Task RejectedOutcome_CarriesNoExecution()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;

        fixture.Pending.Cancel(fixture.UserId, id,
            AgentOperationFingerprint.Compute("create_goal", execution.Arguments.GetRawText())).Should().BeTrue();

        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("rejected");
        outcome.TryGetProperty("Execution", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredOutcome_CarriesNoExecution()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();
        var state = await fixture.Db.PendingAgentOperations.SingleAsync(item => item.Id == id);
        fixture.Db.Entry(state).Property(item => item.ExpiresAtUtc).CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        await fixture.Db.SaveChangesAsync();

        var preview = ConversationTurnRecorder.Preview(state);

        preview.Outcome.Should().Be("expired");
        JsonSerializer.SerializeToElement(preview).TryGetProperty("Execution", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ApprovedExecution_StaysOutOfTheModelContext()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();

        await ConfirmAndExecuteAsync(fixture, id);

        fixture.Db.ChangeTracker.Clear();
        (await StoredOutcomeAsync(fixture, id)).TryGetProperty("Execution", out _).Should().BeTrue();
        var history = (await fixture.HistoryAsync()).Messages.Single(message => message.Role == "assistant");
        using var context = JsonDocument.Parse(history.Content);
        context.RootElement.GetProperty("preview_outcomes").EnumerateArray().Single().EnumerateObject()
            .Select(property => property.Name)
            .Should().Equal("PendingOperationId", "Outcome", "Arguments", "Revision");
    }

    [Fact]
    public async Task OverBudgetExecution_KeepsStatusAndTargetWithinTheTurnBudget()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();
        fixture.Pending.Confirm(fixture.UserId, id).Should().NotBeNull();
        var targetId = Guid.NewGuid().ToString();
        var operation = new AgentOperationResult("bulk_create_habits", "bulk_create_habits", AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.Succeeded, Summary: "Created",
            TargetId: targetId, Payload: new { results = Enumerable.Range(0, 400).Select(index => new { index, title = new string('a', 80) }) });

        fixture.Pending.RecordExecution(fixture.UserId, id, operation).Should().BeTrue();

        fixture.Db.ChangeTracker.Clear();
        var execution = (await StoredOutcomeAsync(fixture, id)).GetProperty("Execution");
        execution.GetRawText().Length.Should().BeLessThanOrEqualTo(PendingAgentOperationState.MaxExecutionJsonLength);
        execution.GetProperty("status").GetString().Should().Be("Succeeded");
        execution.GetProperty("targetId").GetString().Should().Be(targetId);
        execution.GetProperty("summary").GetString().Should().Be("Created");
        var results = execution.GetProperty("payload").GetProperty("results").EnumerateArray().ToArray();
        results.Select(result => result.GetProperty("index").GetInt32()).Should().Equal(Enumerable.Range(0, 400));
        results.Select(result => result.EnumerateObject().Select(property => property.Name).Single()).Should().OnlyContain(name => name == "index");
    }

    [Fact]
    public async Task RecordExecution_UnconfirmedOperation_StoresNothing()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();
        var operation = new AgentOperationResult("create_goal", "create_goal", AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.Succeeded);

        fixture.Pending.RecordExecution(fixture.UserId, id, operation).Should().BeFalse();
        fixture.Pending.RecordExecution(Guid.NewGuid(), id, operation).Should().BeFalse();

        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.PendingAgentOperations.SingleAsync()).ExecutionJson.Should().BeNull();
        (await StoredOutcomeAsync(fixture, id)).TryGetProperty("Execution", out _).Should().BeFalse();
    }

    private static async Task<AgentExecuteOperationResponse> ConfirmAndExecuteAsync(Fixture fixture, Guid id)
    {
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var confirmation = fixture.Pending.Confirm(fixture.UserId, id)!;
        return await fixture.Executor.ExecuteAsync(new(fixture.UserId, execution.OperationId, execution.Arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: confirmation.ConfirmationToken));
    }

    private static async Task<JsonElement> StoredOutcomeAsync(Fixture fixture, Guid id)
    {
        var page = await fixture.PageAsync();
        var turn = page!.Turns.Single(turn => turn.Role == "assistant");
        using var outcomes = JsonDocument.Parse(turn.PreviewOutcomesJson);
        return outcomes.RootElement.EnumerateArray()
            .Single(outcome => outcome.GetProperty("PendingOperationId").GetGuid() == id).Clone();
    }
}
