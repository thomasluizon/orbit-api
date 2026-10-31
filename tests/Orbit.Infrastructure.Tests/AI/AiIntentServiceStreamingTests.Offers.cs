#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Text.Json;
using FluentAssertions;
using OpenAI.Responses;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiIntentServiceStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfferContinuation_EmptyIncompleteAfterReadRetriesTheIdenticalRoundOnce(bool retryAlsoEmpty)
    {
        var empty = ResponsesTestData.Response(inputTokens: 0, outputTokens: 0);
        empty.Id = "resp_empty";
        empty.Status = ResponseStatus.Incomplete;
        empty.IncompleteStatusDetails = new() { Reason = ResponseIncompleteStatusReason.MaxOutputTokens };
        var write = ResponsesTestData.Response();
        write.OutputItems.Add(ResponseItem.CreateFunctionCallItem("write", "create_habit", BinaryData.FromString("{\"title\":\"Water\"}")));
        var handler = new SequenceSseHandler(ToolStream("query_habits", "{}"),
            ResponsesTestData.Stream(empty), retryAlsoEmpty ? ResponsesTestData.Stream(empty) : ResponsesTestData.Stream(write));
        var (service, logger) = BuildServiceWithRecordingLogger(handler);
        var sink = new CollectingSink();
        var first = await service.SendWithToolsAsync(new AiToolRequest("Prepare it.", "system", [new { name = "query_habits" }, new { name = "create_habit" }]), sink.Handle);
        var next = await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!,
            [new("query_habits", "call_1", true, null, null, null)], sink.Handle);

        next.IsSuccess.Should().BeTrue(next.Error);
        handler.RequestBodies.Should().HaveCount(3);
        handler.RequestBodies[2].Should().Be(handler.RequestBodies[1]);
        next.Value.IsTruncated.Should().Be(retryAlsoEmpty);
        if (!retryAlsoEmpty) next.Value.ToolCalls.Should().ContainSingle(call => call.Name == "create_habit");
        logger.WarningProperties.Should().Contain(entry => entry.EventId == 15
            && (string)entry.Properties["ResponseId"]! == "resp_empty"
            && (int?)entry.Properties["InputTokens"] == 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OfferContinuation_ZeroUsageIncompleteDiscardsDoneCallAndRetries(bool missingUsage, bool exhausted)
    {
        var incomplete = ResponsesTestData.Response(inputTokens: 0, outputTokens: 0);
        incomplete.Status = ResponseStatus.Incomplete;
        incomplete.IncompleteStatusDetails = new() { Reason = ResponseIncompleteStatusReason.MaxOutputTokens };
        if (missingUsage) incomplete.Usage = null;
        var discarded = ResponseItem.CreateFunctionCallItem("discarded", "create_habit", BinaryData.FromString("{\"title\":\"Wrong\"}"));
        var failedStream = ResponsesTestData.Event(new StreamingResponseOutputItemDoneUpdate { OutputIndex = 0, Item = discarded })
            + ResponsesTestData.Stream(incomplete);
        var handler = new SequenceSseHandler(ToolStream("query_habits", "{}"), failedStream,
            exhausted ? failedStream : ToolStream("create_habit", "{\"title\":\"Correct\"}"));
        var (service, sink) = BuildService(handler);
        var first = await service.SendWithToolsAsync(new AiToolRequest("Prepare it.", "system", []), sink.Handle);

        var result = await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!,
            [new("query_habits", "call_1", true, null, null, null)], sink.Handle);

        result.IsSuccess.Should().BeTrue(result.Error);
        handler.RequestBodies.Should().HaveCount(3);
        handler.RequestBodies[2].Should().Be(handler.RequestBodies[1]);
        result.Value.IsTruncated.Should().Be(exhausted);
        (result.Value.ToolCalls ?? []).Should().NotContain(call => call.Id == "discarded");
        ((List<ResponseItem>)result.Value.ConversationContext!.Messages).OfType<FunctionCallResponseItem>()
            .Should().NotContain(call => call.CallId == "discarded");
        if (!exhausted)
            result.Value.ToolCalls.Should().ContainSingle(call => call.Args.GetProperty("title").GetString() == "Correct");
    }

    [Fact]
    public async Task OfferContinuation_ZeroUsageWithStreamedTextIsNotRetried()
    {
        var incomplete = ResponsesTestData.Response(inputTokens: 0, outputTokens: 0);
        incomplete.Status = ResponseStatus.Incomplete;
        incomplete.IncompleteStatusDetails = new() { Reason = ResponseIncompleteStatusReason.MaxOutputTokens };
        var handler = new SequenceSseHandler(ResponsesTestData.Stream(incomplete, "Already streamed."));
        var (service, sink) = BuildService(handler);

        var result = await service.SendWithToolsAsync(new AiToolRequest("Prepare it.", "system", []), sink.Handle);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.IsTruncated.Should().BeTrue();
        result.Value.HasToolCalls.Should().BeFalse();
        handler.RequestBodies.Should().ContainSingle();
    }

    [Fact]
    public async Task OfferContinuation_PartialWriteAfterReadRemainsTruncatedWithoutRetryOrExecution()
    {
        var partial = ResponsesTestData.Response(inputTokens: 50, outputTokens: 4096);
        partial.Status = ResponseStatus.Incomplete;
        partial.IncompleteStatusDetails = new() { Reason = ResponseIncompleteStatusReason.MaxOutputTokens };
        partial.OutputItems.Add(ResponseItem.CreateFunctionCallItem("partial", "create_habit", BinaryData.FromString("{\"title\":")));
        var handler = new SequenceSseHandler(ToolStream("query_habits", "{}"), ResponsesTestData.Stream(partial));
        var (service, sink) = BuildService(handler);
        var first = await service.SendWithToolsAsync(new AiToolRequest("Prepare it.", "system", []), sink.Handle);

        var next = await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!,
            [new("query_habits", "call_1", true, null, null, null)], sink.Handle);

        next.IsSuccess.Should().BeTrue(next.Error);
        next.Value.IsTruncated.Should().BeTrue();
        next.Value.HasToolCalls.Should().BeFalse();
        handler.RequestBodies.Should().HaveCount(2);
        ((List<ResponseItem>)next.Value.ConversationContext!.Messages).OfType<FunctionCallResponseItem>()
            .Should().NotContain(call => call.CallId == "partial");
    }

    [Fact]
    public async Task OfferContinuation_ItemDoneIsPreservedWhenTerminalOutputIsEmpty()
    {
        var call = ResponseItem.CreateFunctionCallItem("write", "create_habit", BinaryData.FromString("{\"title\":\"Water\"}"));
        var body = ResponsesTestData.Event(new StreamingResponseOutputItemDoneUpdate { OutputIndex = 0, Item = call })
            + ResponsesTestData.Stream(ResponsesTestData.Response());
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("Prepare it.", "system", []), sink.Handle);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.ToolCalls.Should().ContainSingle(tool => tool.Id == "write" && tool.Name == "create_habit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfferContinuation_EveryCallGetsExactlyOneOutputEvenWhenNotExecuted(bool streaming)
    {
        var first = ResponsesTestData.Response();
        first.OutputItems.Add(ResponseItem.CreateFunctionCallItem("read", "query_habits", BinaryData.FromString("{}")));
        first.OutputItems.Add(ResponseItem.CreateFunctionCallItem("withheld", "create_habit", BinaryData.FromString("{}")));
        string Body(ResponseResult response) => streaming ? ResponsesTestData.Stream(response) : ModelReaderWriter.Write(response).ToString();
        var handler = new SequenceSseHandler(Body(first), Body(ResponsesTestData.Response("Ready.")), Body(ResponsesTestData.Response("Ready.")));
        var (service, sink) = BuildService(handler);
        Func<AiStreamEvent, Task>? stream = streaming ? sink.Handle : null;
        var response = await service.SendWithToolsAsync(new AiToolRequest("Read it.", "system", []), stream);
        var context = response.Value.ConversationContext!;
        var next = await service.ContinueWithToolResultsAsync(context, [new("query_habits", "read", true, null, null, null)], stream);
        next.IsSuccess.Should().BeTrue(next.Error);
        await service.ContinueWithToolResultsAsync(context, [new("query_habits", "read", true, null, null, null)], stream);

        foreach (var body in handler.RequestBodies.Skip(1))
        {
            using var wire = JsonDocument.Parse(body);
            var outputs = wire.RootElement.GetProperty("input").EnumerateArray()
                .Where(item => item.GetProperty("type").GetString() == "function_call_output").ToArray();
            outputs.Select(item => item.GetProperty("call_id").GetString()).Should().BeEquivalentTo("read", "withheld");
            using var failure = JsonDocument.Parse(outputs.Single(item => item.GetProperty("call_id").GetString() == "withheld").GetProperty("output").GetString()!);
            failure.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            failure.RootElement.GetProperty("payload").GetProperty("status").GetString().Should().Be("not_executed");
        }
    }
}
