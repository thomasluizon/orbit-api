using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class FirstTryPreviewTests
{
    private const string LegacyFingerprint = "fingerprint-of-the-localized-form";

    [Theory]
    [InlineData("en", "Skip", "Every 2 hours")]
    [InlineData("pt-BR", "Pular", "A cada 2 horas")]
    public async Task ConversationRecordAndModelText_HoldTheLocalizedValuesOfToday(string locale, string skip, string reminder)
    {
        var client = OfferClient(locale);
        var startedAt = DateTime.UtcNow;
        var (skipPreview, reminderPreview) = await BindSkipAndReminderAsync(client, startedAt);
        var recorder = NewRecorder();
        var turn = new ConversationTurnCapture(_userId, Guid.NewGuid(), startedAt, "Skip and remind", "Both are ready.", "[]", true,
            ConversationId: _conversationId,
            ToolResults: [new("bulk_skip_habits", "skip-call", true, null, null, null, skipPreview),
                new("update_habit", "reminder-call", true, null, null, null, reminderPreview)]);

        (await recorder.RecordAsync(turn)).Should().BeTrue();

        _factory.Context.ChangeTracker.Clear();
        var stored = await _factory.Context.ConversationTurns.AsNoTracking().SingleAsync(row => row.Role == "assistant");
        DisplayedValues(stored.ToolResultsJson).Should().Equal(skip, reminder);
        var history = await recorder.LoadHistoryAsync(_userId, _conversationId);
        DisplayedValues(history.Value.Messages.Single(message => message.Role == "assistant").Content)
            .Should().Equal(skip, reminder);
        var outcomes = JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(stored.PreviewOutcomesJson)!;
        outcomes.Should().HaveCount(2);
        outcomes.Select(outcome => outcome.Arguments.GetRawText()).Should().NotContain(text => text.Contains(skip) || text.Contains(reminder),
            "the stored arguments never hold the localized text");
    }

    [Theory]
    [InlineData("en", "Skip", "Every 2 hours")]
    [InlineData("pt-BR", "Pular", "A cada 2 horas")]
    public async Task RevisePendingChangesPayload_HoldsTheLocalizedValuesOfToday(string locale, string skip, string reminder)
    {
        var client = OfferClient(locale);
        var (skipPreview, reminderPreview) = await BindSkipAndReminderAsync(client, DateTime.UtcNow);
        var services = new ServiceCollection()
            .AddSingleton<IPendingAgentOperationStore>(_store)
            .AddSingleton<IPendingOperationChangePreviewer>(_previewer)
            .AddSingleton(_revisions)
            .AddSingleton<IAgentCatalogService>(new AgentCatalogService(_registry.GetAll()))
            .BuildServiceProvider();
        var tool = new RevisePendingChangesTool(services);

        var skipRevision = await tool.ExecuteAsync(Revision(skipPreview, "{}"), _userId, default, client);
        var reminderRevision = await tool.ExecuteAsync(Revision(reminderPreview, "{\"title\":\"Stretch more\"}"), _userId, default, client);

        skipRevision.Success.Should().BeTrue(skipRevision.Error);
        reminderRevision.Success.Should().BeTrue(reminderRevision.Error);
        DisplayedValues(JsonSerializer.Serialize(skipRevision.Payload, JsonSerializerOptions.Web)).Should().Equal(skip);
        DisplayedValues(JsonSerializer.Serialize(reminderRevision.Payload, JsonSerializerOptions.Web)).Should().Equal(reminder);
    }

    [Theory]
    [InlineData("en", "Skip", "Every 2 hours")]
    [InlineData("pt-BR", "Pular", "A cada 2 horas")]
    public async Task ConversationPage_ReturnsTheLocalizedValuesOfAnExecutedAndAnApprovedRevisedPreview(string locale, string skip, string reminder)
    {
        var client = OfferClient(locale);
        var startedAt = DateTime.UtcNow;
        var (skipPreview, reminderPreview) = await BindSkipAndReminderAsync(client, startedAt);
        (await _revisions.RefreshAsync(_userId, skipPreview.Id, default, client)).IsSuccess.Should().BeTrue();
        (await _revisions.ReviseAsync(_userId, reminderPreview.Id, new(reminderPreview.PreviewFingerprint!,
            [new(reminderPreview.Items![0].ItemId, JsonSerializer.SerializeToElement(new { title = "Stretch more" }))], client), default))
            .IsSuccess.Should().BeTrue();
        Succeeded(await ApproveAsync(skipPreview.Id, client));
        Succeeded(await ApproveAsync(reminderPreview.Id, client));

        var outcomes = await PageOutcomesAsync(startedAt);

        outcomes.Should().HaveCount(2).And.OnlyContain(outcome => outcome.Outcome == "approved");
        var skipOutcome = outcomes.Single(outcome => outcome.PendingOperationId == skipPreview.Id);
        var reminderOutcome = outcomes.Single(outcome => outcome.PendingOperationId == reminderPreview.Id);
        DisplayedValues(skipOutcome.RevisedPreview!.Value.GetRawText()).Should().Equal(skip);
        DisplayedValues(reminderOutcome.RevisedPreview!.Value.GetRawText()).Should().Equal(reminder);
        skipOutcome.Execution.Should().NotBeNull();
        reminderOutcome.Execution.Should().NotBeNull();
        (await StoredExecutionJsonAsync(reminderPreview.Id)).Should().Be(reminderOutcome.Execution!.Value.GetRawText());
    }

    [Theory]
    [InlineData("en", "Skip", "Every 2 hours")]
    [InlineData("pt-BR", "Pular", "A cada 2 horas")]
    public async Task ConversationPage_ReturnsARowFromBefore3aUnchanged(string locale, string skip, string reminder)
    {
        var client = OfferClient(locale);
        var startedAt = DateTime.UtcNow;
        var (skipPreview, reminderPreview) = await BindSkipAndReminderAsync(client, startedAt);
        var catalog = new AgentCatalogService(_registry.GetAll());
        var legacy = new[] { (skipPreview, "bulk_skip_habits", skip, "action"), (reminderPreview, "update_habit", reminder, "repeat_reminder") };
        foreach (var (preview, operationId, text, name) in legacy)
        {
            var stored = _store.GetExecution(_userId, preview.Id)!;
            var revised = (await _previewer.PreviewAsync(_userId, operationId, stored.Arguments, default, client)).Value! with
            {
                PreviewFingerprint = LegacyFingerprint + operationId
            };
            var revision = _store.GetRevision(_userId, preview.Id)!;
            _store.Revise(_userId, preview.Id, revision.OperationFingerprint, revision.ArgumentsJson, revision.OperationFingerprint,
                revised.PreviewFingerprint!, revised).Should().BeTrue();
            _store.Confirm(_userId, preview.Id).Should().NotBeNull();
            _store.RecordExecution(_userId, preview.Id, new AgentOperationResult(operationId, operationId, AgentRiskClass.Low,
                AgentConfirmationRequirement.None, AgentOperationStatus.Succeeded, Payload: new Dictionary<string, string> { [name] = text }))
                .Should().BeTrue();
        }

        var outcomes = await PageOutcomesAsync(startedAt);

        outcomes.Should().HaveCount(2).And.OnlyContain(outcome => outcome.Outcome == "approved");
        foreach (var (preview, _, text, name) in legacy)
        {
            var outcome = outcomes.Single(item => item.PendingOperationId == preview.Id);
            DisplayedValues(outcome.RevisedPreview!.Value.GetRawText()).Should().Equal(text);
            outcome.Execution!.Value.GetProperty("payload").GetProperty(name).GetString().Should().Be(text);
        }
    }

    private async Task<(PendingAgentOperation Skip, PendingAgentOperation Reminder)> BindSkipAndReminderAsync(
        AgentClientContext client, DateTime startedAt)
    {
        var water = AddHabit("Water");
        var other = AddHabit("Stretch");
        var skip = await BindInTurnAsync("bulk_skip_habits",
            new { revised_items = new[] { new { habit_id = water.Id, date = "2026-03-20" } } }, client, startedAt);
        var reminder = await BindInTurnAsync("update_habit",
            new { habit_id = other.Id, repeat_reminder = new { interval_minutes = 120 } }, client, startedAt);
        return (skip, reminder);
    }

    private async Task<PendingAgentOperation> BindInTurnAsync(string operationId, object arguments, AgentClientContext client,
        DateTime startedAt) =>
        Pending(await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, operationId,
            JsonSerializer.SerializeToElement(arguments), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            IncludeChangePreview: true, ClientContext: client)
        {
            PendingOperationConversation = new PendingOperationConversation(_conversationId, startedAt)
        }));

    private static JsonElement Revision(PendingAgentOperation preview, string edits) => JsonSerializer.SerializeToElement(new
    {
        pending_operation_id = preview.Id,
        answer_question = false,
        items = new[] { new { item_id = preview.Items![0].ItemId, edits_json = edits } }
    });

    private ConversationTurnRecorder NewRecorder()
    {
        var flags = Substitute.For<IFeatureFlagService>();
        flags.GetEnabledKeysForUserAsync(_userId, Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns(new[] { FeatureFlagKeys.AiConversationRecording });
        return new ConversationTurnRecorder(_factory.Context, new UnitOfWork(_factory.Context, new DatabaseConnectionSettings()), flags,
            _encryption, Substitute.For<IObjectStorageService>(), Options.Create(new AiSettings { Model = "recording-model" }),
            NullLogger<ConversationTurnRecorder>.Instance);
    }

    private async Task<ConversationPreviewOutcome[]> PageOutcomesAsync(DateTime startedAt)
    {
        var recorder = NewRecorder();
        (await recorder.RecordAsync(new ConversationTurnCapture(_userId, Guid.NewGuid(), startedAt, "Skip and remind", "Both are ready.",
            "[]", true, ConversationId: _conversationId))).Should().BeTrue();
        var page = await recorder.GetLatestAsync(_userId, _conversationId, null, 10);
        return JsonSerializer.Deserialize<ConversationPreviewOutcome[]>(page!.Turns.Single(turn => turn.Role == "assistant").PreviewOutcomesJson)!;
    }

    private async Task<string?> StoredExecutionJsonAsync(Guid pendingId)
    {
        _factory.Context.ChangeTracker.Clear();
        return (await _factory.Context.PendingAgentOperations.AsNoTracking().SingleAsync(row => row.Id == pendingId)).ExecutionJson;
    }

    private static IReadOnlyList<string?> DisplayedValues(string json)
    {
        var values = new List<string?>();
        using var document = JsonDocument.Parse(json);
        Collect(document.RootElement);
        return values.Distinct().ToArray();

        void Collect(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var child in element.EnumerateArray()) Collect(child);
                    break;
                case JsonValueKind.Object:
                    if (element.TryGetProperty("field", out var name) && name.ValueKind == JsonValueKind.String
                        && name.GetString() is "action" or "repeat_reminder" && element.TryGetProperty("newValue", out var value))
                        values.Add(value.GetString());
                    foreach (var property in element.EnumerateObject()) Collect(property.Value);
                    break;
            }
        }
    }
}
