using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Fact]
    public async Task RevisionTool_PatchesPendingBundleInPlaceAndKeepsUnmentionedFieldsAndMembers()
    {
        var pending = await HoldTwoChangesAsync();
        var original = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        var target = pending.Items!.Single(item => item.EntityName == "Magnesium");
        using var services = RevisionToolServices();
        var tool = new RevisePendingChangesTool(services);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            pending_operation_id = pending.Id,
            items = new[] { new { item_id = target.ItemId, edits_json = "{\"due_time\":\"21:30\"}" } }
        });
        _unitOfWork.ClearReceivedCalls();
        tool.IsReadOnly.Should().BeTrue();

        var result = await tool.ExecuteAsync(arguments, _userId, default);

        result.Success.Should().BeTrue(result.Error);
        var revised = result.Payload.Should().BeOfType<PendingAgentOperation>().Subject;
        revised.Id.Should().Be(pending.Id);
        revised.Items.Should().HaveCount(2);
        var before = AccountChangeSet.Read(original.Arguments)!;
        var after = AccountChangeSet.Read(_pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments)!;
        after.Single(member => member.OperationId == "update_habit").Arguments.GetRawText()
            .Should().Be(before.Single(member => member.OperationId == "update_habit").Arguments.GetRawText());
        var child = after.Single(member => member.OperationId == "create_sub_habit").Arguments;
        child.GetProperty("due_time").GetString().Should().Be("21:30");
        child.GetProperty("reminder_times")[0].GetInt32().Should().Be(10);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevisionTool_OnlyExplicitlyRemovedItemsDisappear()
    {
        var pending = await HoldTwoChangesAsync();
        var removed = pending.Items!.Single(item => item.EntityName == "Magnesium");
        using var services = RevisionToolServices();
        var tool = new RevisePendingChangesTool(services);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            pending_operation_id = pending.Id,
            items = Array.Empty<object>(),
            remove_item_ids = new[] { removed.ItemId }
        }), _userId, default);

        result.Success.Should().BeTrue(result.Error);
        result.Payload.Should().BeOfType<PendingAgentOperation>().Which.Items.Should().ContainSingle()
            .Which.EntityName.Should().NotBe("Magnesium");
        var stored = _pendingOperationStore.GetExecution(_userId, pending.Id)!;
        AccountChangeSet.Read(stored.Arguments).Should().ContainSingle().Which.OperationId.Should().Be("update_habit");
        AccountChangeSet.Reservations(stored.Arguments).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("malformed_json", "invalid_revision")]
    [InlineData("duplicate", "invalid_revision")]
    [InlineData("unoffered", "item_not_offered")]
    [InlineData("unoffered_removal", "item_not_offered")]
    [InlineData("invalid_time", null)]
    public async Task RevisionTool_InvalidCorrectionLeavesOriginalBundleUnchanged(string fault, string? expectedError)
    {
        var pending = await HoldTwoChangesAsync();
        var original = _pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments.GetRawText();
        _unitOfWork.ClearReceivedCalls();
        var id = pending.Items!.Single(item => item.EntityName == "Magnesium").ItemId;
        var edits = new List<object>
        {
            new { item_id = fault == "unoffered" ? "unknown" : id,
                edits_json = fault == "malformed_json" ? "{" : "{\"due_time\":\"99:00\"}" }
        };
        if (fault == "duplicate") edits.Add(edits[0]);
        using var services = RevisionToolServices();
        var tool = new RevisePendingChangesTool(services);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            pending_operation_id = pending.Id,
            items = edits,
            remove_item_ids = fault == "unoffered_removal" ? new[] { "unknown" } : Array.Empty<string>()
        }), _userId, default);

        result.Success.Should().BeFalse();
        if (expectedError is not null) result.Error.Should().Be(expectedError);
        _pendingOperationStore.GetExecution(_userId, pending.Id)!.Arguments.GetRawText().Should().Be(original);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public async Task RevisionTool_MissingPendingOperationDoesNotCreateAProposal(string id)
    {
        using var services = RevisionToolServices();
        var tool = new RevisePendingChangesTool(services);

        var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new
        {
            pending_operation_id = id,
            items = Array.Empty<object>()
        }), _userId, default);

        result.Error.Should().Be("pending_operation_not_found");
        _pendingOperationStore.GetAwaiting(_userId).Should().BeEmpty();
    }

    private ServiceProvider RevisionToolServices() => new ServiceCollection()
        .AddSingleton<IPendingAgentOperationStore>(_pendingOperationStore)
        .AddSingleton<IPendingOperationChangePreviewer>(_previewer)
        .AddSingleton<IAgentCatalogService>(_catalogService)
        .AddSingleton(ParentRevisionService()).BuildServiceProvider();
}
