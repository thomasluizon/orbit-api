using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NSubstitute;
using Orbit.Application.Chat.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Chat;

public sealed partial class ChatWriteHoldHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GateSix_CompletionHonorsDependentAndCoalescedWrites(bool complete)
    {
        ParentAccount("en");
        ScriptToolCall("{}", "create_habit");
        AiToolCall Creation(string title, string id, bool done) => new("create_habit", id,
            JsonSerializer.SerializeToElement(new { title, frequency_unit = "Day", frequency_quantity = 1, orbit_write_complete = done }));
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse
            {
                ToolCalls = complete ? [Creation("Read", "read", true), Creation("Stretch", "stretch", true)] : [Creation("Read", "read", false)],
                ConversationContext = new()
            }));
        _aiIntentService.ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(), Arg.Any<IReadOnlyList<AiToolCallResult>>(),
            Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new AiResponse { ToolCalls = [Creation("Stretch", "stretch", true)], ConversationContext = new() }));

        var result = await CreateHandler().Handle(new(_userId, "Create two daily habits: Read and Stretch"), default);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.PendingOperations.Should().ContainSingle().Which.Items.Should().HaveCount(2);
        await _aiIntentService.Received(complete ? 0 : 1).ContinueWithToolResultsAsync(Arg.Any<AiConversationContext>(),
            Arg.Any<IReadOnlyList<AiToolCallResult>>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en", false)]
    [InlineData("pt-BR", false)]
    [InlineData("en", true)]
    [InlineData("pt-BR", true)]
    public async Task GateSix_StructuredWritesRetainRevisionToolsAndPendingContext(string language, bool revise)
    {
        ParentAccount(language);
        ScriptToolCall("""{"title":"Earlier appointment","frequency_unit":"OneTime","due_time":"09:00"}""");
        var handler = CreateHandler();
        var first = await handler.Handle(new(_userId, "Create a task called Earlier appointment at 09:00"), default);
        var old = first.Value.PendingOperations!.Single();
        var original = _pendingOperationStore.GetExecution(_userId, old.Id)!.Arguments.GetRawText();
        var captured = GateSixCapture("composition-medical-batch-en-r1");
        var args = JsonNode.Parse(captured.GetProperty("toolCalls")[0].GetProperty("arguments").GetRawText())!.AsObject();
        args["orbit_write_complete"] = true;
        var responseCall = revise
            ? new AiToolCall("revise_pending_changes", "revision", JsonSerializer.SerializeToElement(new
            {
                pending_operation_id = old.Id,
                items = new[] { new { item_id = old.Items!.Single().ItemId, edits_json = "{\"due_time\":\"16:00\"}" } },
                orbit_write_complete = true
            }))
            : new AiToolCall("bulk_create_habits", "fresh", JsonSerializer.SerializeToElement(args));
        AiToolRequest? sent = null;
        _aiIntentService.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.Arg<AiToolRequest>();
                return Result.Success(new AiResponse
                {
                    ToolCalls = [responseCall],
                    ConversationContext = new()
                });
            });

        var message = language == "en"
            ? "10/26 cholesterol exam at 15:50 at home. 10/30 other blood tests at 13:30 at home. For the exam on 10/30: stop multivitamins three days before, skip Muay Thai 24 hours before and fast until collection."
            : "dia 26/10, exame de colesterol as 15h50, domiciliar dia 30/10, outros exames de sangue as 13h30, domiciliar PRO EXAME DO DIA 30: 3 dias antes, parar com multivitaminico 24 horas antes, nao ir pro muay thay ficar em jejum puro até a coleta";
        var result = await handler.Handle(new(_userId, message), default);

        sent!.DynamicContext.Should().Contain(old.Id.ToString());
        JsonSerializer.SerializeToElement(sent.ToolDeclarations).EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).Should()
            .BeEquivalentTo(_toolRegistry.GetAll().Select(tool => tool.Name));
        result.IsSuccess.Should().BeTrue(result.Error);
        var pending = result.Value.PendingOperations.Should().ContainSingle().Which;
        if (revise)
        {
            pending.Id.Should().Be(old.Id);
            var expected = JsonNode.Parse(original)!.AsObject();
            expected["due_time"] = "16:00";
            JsonElement.DeepEquals(_pendingOperationStore.GetExecution(_userId, old.Id)!.Arguments,
                JsonSerializer.SerializeToElement(expected)).Should().BeTrue();
        }
        else
        {
            pending.Id.Should().NotBe(old.Id);
            pending.Items.Should().HaveCount(5);
            _pendingOperationStore.GetExecution(_userId, old.Id)!.Arguments.GetRawText().Should().Be(original);
        }
    }
}
