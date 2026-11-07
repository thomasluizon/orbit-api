using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Habits.Commands;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class PlanCommittedOutcomeTests
{
    private static readonly string[] ClientBulkItemKeys = ["error", "field", "habitId", "index", "status", "title"];
    private static readonly object ReadPagesHabit = new { title = "Ler 20 paginas 2048", frequency_unit = "Day", frequency_quantity = 1 };

    [Fact]
    public async Task RevisedThenApprovedCreate_ConversationPageCarriesTheApprovedPreviewWords()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(operationId: "create_habit", toolArguments: ReadPagesHabit);
        var held = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var original = (await fixture.Previewer.PreviewAsync(fixture.UserId, "create_habit", held.Arguments)).Value!;

        var revised = await fixture.Revisions.ReviseAsync(fixture.UserId, id, new(original.PreviewFingerprint!,
            [new(original.Items!.Single().ItemId, JsonSerializer.SerializeToElement(new { title = "Ler 30 paginas 2048" }))]), default);
        revised.IsSuccess.Should().BeTrue(revised.Error);
        var response = await ConfirmAndExecuteAsync(fixture, id);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, JsonSerializer.Serialize(response));
        fixture.Db.ChangeTracker.Clear();
        (await fixture.Db.Habits.SingleAsync()).Title.Should().Be("Ler 30 paginas 2048");
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("approved");
        var approved = outcome.GetProperty("RevisedPreview");
        JsonNode.DeepEquals(JsonNode.Parse(approved.GetRawText()), JsonSerializer.SerializeToNode(revised.Preview, WireOptions))
            .Should().BeTrue("the stored preview must equal the preview of the live revise response");
        approved.GetProperty("items").EnumerateArray().Single().GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("field").GetString() == "title")
            .GetProperty("newValue").GetString().Should().Be("Ler 30 paginas 2048");
        _output.WriteLine(outcome.GetRawText());
    }

    [Fact]
    public async Task ApprovedCreateWithoutRevision_CarriesNoRevisedPreview()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(operationId: "create_habit", toolArguments: ReadPagesHabit);

        var response = await ConfirmAndExecuteAsync(fixture, id);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, JsonSerializer.Serialize(response));
        fixture.Db.ChangeTracker.Clear();
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("approved");
        outcome.TryGetProperty("Execution", out _).Should().BeTrue();
        outcome.TryGetProperty("RevisedPreview", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RevisedPreview_StaysOutOfTheModelContextAndKeepsTheInstalledClientFields()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(operationId: "create_habit", toolArguments: ReadPagesHabit);
        var held = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var original = (await fixture.Previewer.PreviewAsync(fixture.UserId, "create_habit", held.Arguments)).Value!;
        (await fixture.Revisions.ReviseAsync(fixture.UserId, id, new(original.PreviewFingerprint!,
            [new(original.Items!.Single().ItemId, JsonSerializer.SerializeToElement(new { title = "Ler 30 paginas 2048" }))]), default))
            .IsSuccess.Should().BeTrue();

        await ConfirmAndExecuteAsync(fixture, id);

        fixture.Db.ChangeTracker.Clear();
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("PendingOperationId").GetGuid().Should().Be(id);
        outcome.GetProperty("Outcome").ValueKind.Should().Be(JsonValueKind.String);
        outcome.GetProperty("Arguments").GetProperty("title").GetString().Should().Be("Ler 30 paginas 2048");
        outcome.GetProperty("Revision").ValueKind.Should().Be(JsonValueKind.Number);
        outcome.GetProperty("RevisedPreview").ValueKind.Should().Be(JsonValueKind.Object);
        var history = (await fixture.HistoryAsync()).Messages.Single(message => message.Role == "assistant");
        using var context = JsonDocument.Parse(history.Content);
        context.RootElement.GetProperty("preview_outcomes").EnumerateArray().Single().EnumerateObject()
            .Select(property => property.Name)
            .Should().Equal("PendingOperationId", "Outcome", "Arguments", "Revision");
    }

    [Fact]
    public async Task HundredItemBulkCreate_WithFailedItems_KeepsEveryItemStatusAndPartial()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync();
        fixture.Pending.Confirm(fixture.UserId, id).Should().NotBeNull();
        var results = Enumerable.Range(0, 100).Select(index => index % 25 == 7
            ? new BulkCreateItemResult(index, BulkItemStatus.Failed, Title: $"Habit number {index} with a long title",
                Error: "Title is invalid for this habit and cannot be saved.", Field: "title", ErrorCode: "invalid_title")
            : new BulkCreateItemResult(index, BulkItemStatus.Success, Guid.NewGuid(), $"Habit number {index} with a long title")).ToArray();
        var payload = new BulkCreateResult(results);
        var operation = new AgentOperationResult("bulk_create_habits", "bulk_create_habits", AgentRiskClass.Low,
            AgentConfirmationRequirement.FreshConfirmation, AgentOperationStatus.Succeeded, Summary: "96/100 habits created",
            Payload: payload);
        JsonSerializer.Serialize(operation, WireOptions).Length.Should().BeGreaterThan(PendingAgentOperationState.MaxExecutionJsonLength,
            "the full result must be over the stored budget for this case");

        fixture.Pending.RecordExecution(fixture.UserId, id, operation).Should().BeTrue();

        fixture.Db.ChangeTracker.Clear();
        var execution = (await StoredOutcomeAsync(fixture, id)).GetProperty("Execution");
        execution.GetRawText().Length.Should().BeLessThanOrEqualTo(PendingAgentOperationState.MaxExecutionJsonLength);
        execution.GetProperty("status").GetString().Should().Be("Succeeded");
        var stored = execution.GetProperty("payload");
        stored.GetProperty("partial").GetBoolean().Should().BeTrue();
        var items = stored.GetProperty("results").EnumerateArray().ToArray();
        items.Select(item => item.GetProperty("index").GetInt32()).Should().Equal(Enumerable.Range(0, 100));
        items.Select(item => item.GetProperty("status").GetString())
            .Should().Equal(results.Select(item => item.Status.ToString()));
        items.Select(item => item.GetProperty("habitId").ValueKind == JsonValueKind.Null ? null : item.GetProperty("habitId").GetString())
            .Should().Equal(results.Select(item => item.HabitId?.ToString()));
        items.Should().OnlyContain(item => item.EnumerateObject().Select(property => property.Name).Order()
            .SequenceEqual(ClientBulkItemKeys), "each item must parse with the client bulk create item schema");
        items.Should().OnlyContain(item => item.GetProperty("title").ValueKind == JsonValueKind.Null
            && item.GetProperty("error").ValueKind == JsonValueKind.Null && item.GetProperty("field").ValueKind == JsonValueKind.Null);
        _output.WriteLine(execution.GetRawText());
    }

    private readonly ITestOutputHelper _output;

    public PlanCommittedOutcomeTests(ITestOutputHelper output) => _output = output;
}
