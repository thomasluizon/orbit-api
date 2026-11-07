using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class PlanCommittedOutcomeTests
{
    private static object BatchArguments(int count, int titleLength = 0) => new
    {
        habits = Enumerable.Range(0, count).Select(index => new
        {
            title = titleLength == 0 ? $"Ler {index} paginas por dia" : $"{index:D3}".PadRight(titleLength, 'a'),
            description = $"Ler um livro por {index} minutos antes de dormir",
            emoji = "📚",
            frequency_unit = "Day",
            frequency_quantity = 1,
            due_time = "21:30",
            days = new[] { "Monday", "Wednesday", "Friday" }
        }).ToArray()
    };

    private static async Task<(Guid Id, PendingOperationChangePreview Original)> ProposeBatchAsync(Fixture fixture, int count,
        int titleLength = 0)
    {
        var id = await fixture.ProposeAsync(operationId: "bulk_create_habits", toolArguments: BatchArguments(count, titleLength));
        var held = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var original = (await fixture.Previewer.PreviewAsync(fixture.UserId, "bulk_create_habits", held.Arguments)).Value!;
        original.Items.Should().HaveCount(count);
        return (id, original);
    }

    private static Task<Orbit.Application.Chat.PendingOperationRevisionResult> RenameAllAsync(Fixture fixture, Guid id,
        PendingOperationChangePreview original, string suffix) =>
        fixture.Revisions.ReviseAsync(fixture.UserId, id, new(original.PreviewFingerprint!, original.Items!
            .Select(item => new Orbit.Application.Chat.RevisedPendingOperationItem(item.ItemId,
                JsonSerializer.SerializeToElement(new { title = item.EntityName + suffix }))).ToArray()), default);

    [Fact]
    public async Task UneditedApproval_CarriesPreviewRevisedFalse()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(operationId: "create_habit", toolArguments: ReadPagesHabit);

        (await ConfirmAndExecuteAsync(fixture, id)).Operation.Status.Should().Be(AgentOperationStatus.Succeeded);

        fixture.Db.ChangeTracker.Clear();
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("approved");
        outcome.GetProperty("PreviewRevised").GetBoolean().Should().BeFalse();
        outcome.TryGetProperty("RevisedPreview", out _).Should().BeFalse();
    }

    [Fact]
    public async Task RevisedApproval_CarriesPreviewRevisedTrue()
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
        outcome.GetProperty("PreviewRevised").GetBoolean().Should().BeTrue();
        outcome.GetProperty("RevisedPreview").ValueKind.Should().Be(JsonValueKind.Object);
        _output.WriteLine(outcome.GetRawText());
    }

    [Fact]
    public async Task RefreshedApproval_StoresTheRefreshedPreviewAndCarriesPreviewRevisedTrue()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(operationId: "create_habit", toolArguments: ReadPagesHabit);

        var refreshed = await fixture.Revisions.RefreshAsync(fixture.UserId, id, default);
        refreshed.IsSuccess.Should().BeTrue(refreshed.Error);
        fixture.Db.ChangeTracker.Clear();
        var stored = (await fixture.Db.PendingAgentOperations.SingleAsync(item => item.Id == id)).RevisedPreviewJson;
        stored.Should().NotBeNull("a refresh stores its preview the same way a revision does");
        JsonSerializer.Deserialize<JsonElement>(stored!).GetProperty("previewFingerprint").GetString()
            .Should().Be(refreshed.Preview!.PreviewFingerprint);
        await ConfirmAndExecuteAsync(fixture, id);

        fixture.Db.ChangeTracker.Clear();
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("approved");
        outcome.GetProperty("PreviewRevised").GetBoolean().Should().BeTrue();
        outcome.GetProperty("RevisedPreview").GetProperty("items").EnumerateArray().Single()
            .GetProperty("entityName").GetString().Should().Be("Ler 20 paginas 2048");
    }

    [Fact]
    public async Task RevisedApproval_WhenThePreviewDoesNotFit_StillCarriesPreviewRevisedTrue()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(operationId: "create_habit", toolArguments: ReadPagesHabit);
        var held = fixture.Pending.GetExecution(fixture.UserId, id)!;
        var fingerprint = AgentOperationFingerprint.Compute("create_habit", held.Arguments.GetRawText());
        var title = new string('a', AppConstants.MaxHabitTitleLength);
        var oversized = new PendingOperationChangePreview([], 1, Enumerable.Range(0, 400).Select(index =>
            new PendingOperationItem(index.ToString(), null, title,
                [new PendingOperationChange(Guid.Empty, title, "title", title, title, "text", JsonSerializer.SerializeToElement(title))],
                new string('f', 64))).ToArray(), "oversized");

        fixture.Pending.Revise(fixture.UserId, id, fingerprint, held.Arguments.GetRawText(), fingerprint, "oversized", oversized)
            .Should().BeTrue();
        await ConfirmAndExecuteAsync(fixture, id);

        fixture.Db.ChangeTracker.Clear();
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("approved");
        outcome.TryGetProperty("RevisedPreview", out _).Should().BeFalse();
        outcome.GetProperty("PreviewRevised").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ServerPreviewBinding_DoesNotMarkThePreviewRevised()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(operationId: "create_habit", toolArguments: ReadPagesHabit);

        fixture.Db.ChangeTracker.Clear();
        var state = await fixture.Db.PendingAgentOperations.SingleAsync(item => item.Id == id);
        state.RevisionNumber.Should().BeGreaterThan(0, "the fixture binds the preview through the store like the executor");
        state.PreviewRevised.Should().BeFalse();
        state.RevisedPreviewJson.Should().BeNull();
    }

    [Fact]
    public async Task PendingAndRejectedOutcomes_CarryNoPreviewRevised()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(operationId: "create_habit", toolArguments: ReadPagesHabit);
        (await fixture.Revisions.RefreshAsync(fixture.UserId, id, default)).IsSuccess.Should().BeTrue();

        (await StoredOutcomeAsync(fixture, id)).TryGetProperty("PreviewRevised", out _).Should().BeFalse();
        var execution = fixture.Pending.GetExecution(fixture.UserId, id)!;
        fixture.Pending.Cancel(fixture.UserId, id,
            AgentOperationFingerprint.Compute("create_habit", execution.Arguments.GetRawText())).Should().BeTrue();

        fixture.Db.ChangeTracker.Clear();
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("Outcome").GetString().Should().Be("rejected");
        outcome.TryGetProperty("PreviewRevised", out _).Should().BeFalse();
    }

    [Fact]
    public async Task PreviewRevised_StaysOutOfTheModelContext()
    {
        using var fixture = new Fixture();
        var id = await fixture.ProposeAsync(operationId: "create_habit", toolArguments: ReadPagesHabit);

        await ConfirmAndExecuteAsync(fixture, id);

        fixture.Db.ChangeTracker.Clear();
        (await StoredOutcomeAsync(fixture, id)).TryGetProperty("PreviewRevised", out _).Should().BeTrue();
        var history = (await fixture.HistoryAsync()).Messages.Single(message => message.Role == "assistant");
        using var context = JsonDocument.Parse(history.Content);
        context.RootElement.GetProperty("preview_outcomes").EnumerateArray().Single().EnumerateObject()
            .Select(property => property.Name)
            .Should().Equal("PendingOperationId", "Outcome", "Arguments", "Revision");
    }

    [Fact]
    public async Task EditedHundredItemBatch_StoresTheSmallestPreviewTierWithEveryItemTitle()
    {
        using var fixture = new Fixture();
        var (id, original) = await ProposeBatchAsync(fixture, AppConstants.MaxBulkOperationSize);

        var revised = await RenameAllAsync(fixture, id, original, " (editado)");

        revised.IsSuccess.Should().BeTrue(revised.Error);
        var full = JsonSerializer.Serialize(revised.Preview, WireOptions).Length;
        full.Should().BeGreaterThan(PendingOperationExecutionJson.RichPreviewJsonLength, "the full preview must be over the rich budget");
        fixture.Db.ChangeTracker.Clear();
        var stored = (await fixture.Db.PendingAgentOperations.SingleAsync(item => item.Id == id)).RevisedPreviewJson;
        stored.Should().NotBeNull();
        stored!.Length.Should().BeLessThanOrEqualTo(PendingAgentOperationState.MaxRevisedPreviewJsonLength);
        _output.WriteLine($"100 item preview: full {full} characters, stored smallest tier {stored.Length} characters");
        var items = JsonSerializer.Deserialize<JsonElement>(stored).GetProperty("items").EnumerateArray().ToArray();
        items.Select(item => item.GetProperty("itemId").GetString()).Should().Equal(revised.Preview!.Items!.Select(item => item.ItemId));
        items.Select(item => item.GetProperty("entityName").GetString())
            .Should().Equal(revised.Preview.Items!.Select(item => item.EntityName));
        items.Should().OnlyContain(item => item.GetProperty("entityName").GetString()!.EndsWith(" (editado)"));
        items.Select(item => item.EnumerateObject().Any(property => property.Name == "entityId")).Should().OnlyContain(present => present);
        items.Select(item => item.GetProperty("fields").EnumerateArray().Single().GetProperty("newValue").GetString())
            .Should().Equal(revised.Preview.Items!.Select(item => item.EntityName));

        await ConfirmAndExecuteAsync(fixture, id);

        fixture.Db.ChangeTracker.Clear();
        var outcome = await StoredOutcomeAsync(fixture, id);
        outcome.GetProperty("PreviewRevised").GetBoolean().Should().BeTrue();
        outcome.GetProperty("RevisedPreview").GetProperty("items").GetArrayLength().Should().Be(AppConstants.MaxBulkOperationSize);
    }

    [Fact]
    public void SmallestPreviewTier_FitsHundredItemsAtTheLongestTitle()
    {
        var title = new string('a', AppConstants.MaxHabitTitleLength);
        var renamed = new string('b', AppConstants.MaxHabitTitleLength);
        var items = Enumerable.Range(0, AppConstants.MaxBulkOperationSize).Select(index => new PendingOperationItem(
            index.ToString(), Guid.NewGuid(), renamed,
            [
                new PendingOperationChange(Guid.NewGuid(), renamed, "title", title, renamed, "text",
                    JsonSerializer.SerializeToElement(renamed), true, EntityEmoji: "📚"),
                new PendingOperationChange(Guid.NewGuid(), renamed, "description", null, title, "text",
                    JsonSerializer.SerializeToElement(title), true, EntityEmoji: "📚")
            ],
            new string('f', 64), RemovesData: false, EntityEmoji: "📚", HabitType: "recurring")).ToArray();

        var json = PendingOperationExecutionJson.SerializePreview(new PendingOperationChangePreview(
            items.SelectMany(item => item.Fields).ToArray(), items.Length, items, "batch"));

        json.Should().NotBeNull();
        json!.Length.Should().BeLessThanOrEqualTo(PendingAgentOperationState.MaxRevisedPreviewJsonLength);
        _output.WriteLine($"100 items at {AppConstants.MaxHabitTitleLength} characters: smallest tier {json.Length} characters");
        var stored = JsonSerializer.Deserialize<JsonElement>(json).GetProperty("items").EnumerateArray().ToArray();
        stored.Should().HaveCount(AppConstants.MaxBulkOperationSize);
        stored.Should().OnlyContain(item => item.GetProperty("fields").EnumerateArray().Single().GetProperty("field").GetString() == "title"
            && item.GetProperty("fields")[0].GetProperty("newValue").GetString() == renamed
            && item.GetProperty("entityName").GetString() == renamed);
    }
}
