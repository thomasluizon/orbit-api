using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Common;
using Orbit.Domain.Enums;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.Services;

public sealed partial class FirstTryPreviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuggestedFieldSetAfterBind_ApprovalIsStaleWithoutASecondPreviewAndARefreshApproves(bool race)
    {
        var habit = AddHabit("Read");
        var preview = Pending(await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, "update_habit",
            JsonSerializer.SerializeToElement(new { habit_id = habit.Id, title = "Read daily", suggested_fields = new { emoji = "💧" } }),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true, ClientContext: OfferClient())));
        preview.Items!.Single().Fields.Should().Contain(field => field.Field == "emoji" && field.Provenance == "suggested");
        var rows = PendingRows();
        async Task SetEmoji() => await _factory.Context.Habits.Where(item => item.Id == habit.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Emoji, "🥤"));
        if (race) _executor.BeforeNextExecute = SetEmoji;
        else await SetEmoji();

        var stale = await ApproveAsync(preview.Id, OfferClient());

        StalePreview(stale);
        PendingRows().Should().Be(rows, "a stale approval never proposes the change again");
        (await _factory.Context.PendingAgentOperations.AsNoTracking().SingleAsync(item => item.Id == preview.Id))
            .ExecutionJson.Should().BeNull("a stale approval records no execution");
        await _plans.DidNotReceiveWithAnyArgs().ChangeAsync(default, default, default!, default);
        var refreshed = await _revisions.RefreshAsync(_userId, preview.Id, default, OfferClient());
        refreshed.IsSuccess.Should().BeTrue(refreshed.Error);
        Succeeded(await ApproveAsync(preview.Id, OfferClient()));
        (await _factory.Context.Habits.AsNoTracking().SingleAsync()).Title.Should().Be("Read daily");
    }

    [Fact]
    public async Task ChangeSetMemberStaleAtExecute_Returns409WithoutARecordAndARefreshApproves()
    {
        var existing = AddHabit("Read");
        var preview = await BindWithoutClientContextAsync("preview-all-set", existing.Id);
        var rows = PendingRows();
        _executor.BeforeNextExecute = async () => await _factory.Context.Habits.Where(item => item.Id == existing.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Title, "Read books"));

        var stale = await ApproveAsync(preview.Id, PlainClient);

        StalePreview(stale);
        PendingRows().Should().Be(rows);
        (await _factory.Context.PendingAgentOperations.AsNoTracking().SingleAsync(item => item.Id == preview.Id))
            .ExecutionJson.Should().BeNull();
        await _plans.DidNotReceiveWithAnyArgs().ChangeAsync(default, default, default!, default);
        (await _revisions.RefreshAsync(_userId, preview.Id, default, PlainClient)).IsSuccess.Should().BeTrue();
        Succeeded(await ApproveAsync(preview.Id, PlainClient));
        (await _factory.Context.Habits.AsNoTracking().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task ReplacedToken_Returns409WithoutAPendingOperationAndTheNewTokenApproves()
    {
        var preview = await BindWithoutClientContextAsync("picklist-create", Guid.Empty);
        var first = _store.Confirm(_userId, preview.Id)!;
        var second = _store.Confirm(_userId, preview.Id)!;
        var rows = PendingRows();

        var stale = await _controller.ExecutePendingOperation(preview.Id,
            new Orbit.Api.Controllers.AiController.ExecutePendingOperationRequest(first.ConfirmationToken, PlainClient), default);

        StalePreview(stale);
        PendingRows().Should().Be(rows, "a token that confirms no stored operation creates no pending operation");
        (await _factory.Context.Habits.AsNoTracking().CountAsync()).Should().Be(0);
        Succeeded(await _controller.ExecutePendingOperation(preview.Id,
            new Orbit.Api.Controllers.AiController.ExecutePendingOperationRequest(second.ConfirmationToken, PlainClient), default));
        (await _factory.Context.Habits.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task StepUpOperationBeforeItsStepUp_StillReturnsStepUpRequired()
    {
        _bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(AgentConfirmationRequirement.StepUp);
        var preview = await BindWithoutClientContextAsync("picklist-create", Guid.Empty);

        var result = await ApproveAsync(preview.Id, PlainClient);

        var response = result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<AgentExecuteOperationResponse>().Subject;
        response.Operation.PolicyReason.Should().Be("step_up_required");
        (await _factory.Context.Habits.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ChatFormTokenThatConfirmsNothing_BindsANewCreateWithRequestedFieldsCaptured()
    {
        var result = await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, "create_habit",
            JsonSerializer.SerializeToElement(new { title = "Journal", frequency_unit = "Day", frequency_quantity = 1 }),
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, ConfirmationToken: "agc_unknown", IncludeChangePreview: true,
            ClientContext: OfferClient()));

        var preview = Pending(result);
        var stored = _store.GetExecution(_userId, preview.Id)!;
        stored.Arguments.GetProperty("orbit_offer_metadata").GetProperty("requested_fields").EnumerateArray()
            .Select(field => field.GetString()).Should().Contain("title");
        (await _revisions.IsCurrentAsync(_userId, stored, default, OfferClient())).Should().BeTrue();
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("repeat_reminder")]
    public async Task PreviewBoundBeforeTheHashView_IsStaleOnceAndARefreshMakesItApprovable(string value)
    {
        var habit = AddHabit("Water");
        var arguments = value == "skip"
            ? JsonSerializer.SerializeToElement(new { revised_items = new[] { new { habit_id = habit.Id, date = "2026-03-20" } } })
            : JsonSerializer.SerializeToElement(new { habit_id = habit.Id, repeat_reminder = new { interval_minutes = 120 } });
        var operation = value == "skip" ? "bulk_skip_habits" : "update_habit";
        var preview = Pending(await _executor.ExecuteAsync(new AgentExecuteOperationRequest(_userId, operation, arguments,
            AgentExecutionSurface.Chat, AgentAuthMethod.Jwt, IncludeChangePreview: true, ClientContext: OfferClient())));
        var stored = _store.GetExecution(_userId, preview.Id)!;
        var argumentFingerprint = AgentOperationFingerprint.Compute(operation, stored.Arguments.GetRawText());
        var legacyFingerprint = AgentOperationFingerprint.Compute(operation, JsonSerializer.Serialize(preview.Items));
        legacyFingerprint.Should().NotBe(preview.PreviewFingerprint, "the old form hashed the localized values");
        _store.Revise(_userId, preview.Id, argumentFingerprint, stored.Arguments.GetRawText(), argumentFingerprint,
            legacyFingerprint).Should().BeTrue();

        StalePreview(await ApproveAsync(preview.Id, OfferClient()));
        (await _revisions.RefreshAsync(_userId, preview.Id, default, OfferClient())).IsSuccess.Should().BeTrue();
        Succeeded(await ApproveAsync(preview.Id, OfferClient()));
    }
}
