using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class FirstTryPreviewTests
{
    [Theory]
    [InlineData("skip", "en", "Skip")]
    [InlineData("skip", "pt-BR", "Pular")]
    [InlineData("reminder", "en", "Every 2 hours")]
    [InlineData("reminder", "pt-BR", "A cada 2 horas")]
    [InlineData("offer-repeat", "en", "Every 2 hours")]
    [InlineData("offer-repeat", "pt-BR", "A cada 2 horas")]
    public async Task ChatTurnChangeSet_FromAClientWithOffersKeepsTodaysTextOneFingerprintAndApproves(string kind, string locale,
        string text)
    {
        var water = AddHabit("Water");
        var other = AddHabit("Stretch");
        var client = OfferClient(locale);
        var rename = await BindAsync("update_habit", new { habit_id = water.Id, title = "Water bottle" }, client);
        var second = kind switch
        {
            "skip" => await BindAsync("bulk_skip_habits",
                new { revised_items = new[] { new { habit_id = other.Id, date = "2026-03-20" } } }, client),
            "reminder" => await BindAsync("update_habit",
                new { habit_id = other.Id, repeat_reminder = new { interval_minutes = 120 } }, client),
            _ => await BindAsync("bulk_create_habits", new
            {
                habits = new[]
                {
                    new
                    {
                        title = "Drink water", is_general = true,
                        suggested_fields = new { repeat_reminder = new { interval_minutes = 120 }, reminder_enabled = true }
                    }
                }
            }, client)
        };

        var combined = await _changeSets.CombineAsync(_userId, [rename, second], default, client);

        combined.Should().NotBeNull();
        var set = combined!.PendingOperation;
        var values = set.Items!.SelectMany(item => item.Fields)
            .Where(field => field.Field is "action" or "repeat_reminder").Select(field => field.NewValue).ToArray();
        values.Should().ContainSingle().Which.Should().StartWith(text);
        set.Changes!.Where(field => field.Field is "action" or "repeat_reminder").Select(field => field.NewValue)
            .Should().Equal(values);
        var stored = _store.GetExecution(_userId, set.Id)!;
        stored.PreviewFingerprint.Should().Be(set.PreviewFingerprint);
        var otherLocale = await _previewer.PreviewAsync(_userId, stored.OperationId, stored.Arguments, default,
            OfferClient(locale == "en" ? "pt-BR" : "en"));
        otherLocale.Value!.PreviewFingerprint.Should().Be(stored.PreviewFingerprint, "one change set has one fingerprint in each locale");
        if (kind == "offer-repeat")
        {
            ProactiveProposalContains(stored.Arguments).Should().BeTrue("a member holds offer data, so the offer step runs at approval");
            var rebuilt = await _previewer.PreviewAsync(_userId, stored.OperationId, stored.Arguments, default, client);
            AgentOperationFingerprint.Compute(stored.OperationId, rebuilt.Value!.ExecutionArguments!.Value.GetRawText())
                .Should().Be(AgentOperationFingerprint.Compute(stored.OperationId, stored.Arguments.GetRawText()),
                    "the offer step returns the stored set arguments unchanged");
        }
        var rows = PendingRows();

        Succeeded(await ApproveAsync(set.Id, client));

        PendingRows().Should().Be(rows);
        (await _factory.Context.Habits.AsNoTracking().SingleAsync(habit => habit.Id == water.Id)).Title.Should().Be("Water bottle");
    }

    private async Task<PendingAgentOperation> BindAsync(string operationId, object arguments, AgentClientContext client) =>
        Pending(await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, operationId,
            JsonSerializer.SerializeToElement(arguments), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            IncludeChangePreview: true, ClientContext: client)));

    private static bool ProactiveProposalContains(JsonElement arguments) => ProactiveProposal.Contains(arguments);

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task PickListCreateFromAClientWithOffers_ApprovesAndRevisesWithoutAStalePreview(string locale)
    {
        var client = OfferClient(locale);
        var item = new AiBlockItem("draft", "Journal", JsonSerializer.SerializeToElement(new
        {
            title = "Journal",
            frequency_unit = "Day",
            frequency_quantity = 1
        }));
        var bind = () => _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, "bulk_create_habits",
            AskTool.BuildBulkArguments("create_habits", [item]), AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            IncludeChangePreview: true, PreviewOnly: true, ClientContext: client)
        {
            PendingOperationConversation = new PendingOperationConversation(_conversationId, DateTime.UtcNow)
        });
        var revised = Pending(await bind());
        var approved = Pending(await bind());

        var edit = await _revisions.ReviseAsync(_userId, revised.Id, new(revised.PreviewFingerprint!,
            [new(revised.Items![0].ItemId, JsonSerializer.SerializeToElement(new { title = "Journal daily" }))], client), default);

        edit.IsSuccess.Should().BeTrue(edit.Error);
        Succeeded(await ApproveAsync(approved.Id, client));
    }

    [Fact]
    public async Task OfferIssuedOnAPreview_StaysCurrentWhenSelectedInTheOtherLocale()
    {
        var habit = AddHabit("Water");
        var preview = await BindAsync("bulk_skip_habits",
            new { revised_items = new[] { new { habit_id = habit.Id, date = "2026-03-20" } } }, OfferClient("en"));
        var revision = preview.PreviewFingerprint;
        var stored = _store.GetExecution(_userId, preview.Id)!;

        var selected = await _previewer.PreviewAsync(_userId, stored.OperationId, stored.Arguments, default, OfferClient("pt-BR"));

        selected.Value!.Items!.Single().Fields.Should().Contain(field => field.Field == "action" && field.NewValue == "Pular");
        selected.Value.PreviewFingerprint.Should().Be(revision);
    }
}
