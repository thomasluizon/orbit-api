#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public class AiIntentServiceStreamingTests
{
    [Fact]
    public async Task SendWithToolsAsync_StreamingTextRound_EmitsDeltasAndReturnsFullText()
    {
        var body = TextStream("Hello!", "Hel", "lo!");
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("Hello!");
        result.Value.HasToolCalls.Should().BeFalse();
        sink.Events.Should().SatisfyRespectively(
            first => { first.Kind.Should().Be(AiStreamEventKind.Delta); first.Text.Should().Be("Hel"); },
            second => { second.Kind.Should().Be(AiStreamEventKind.Delta); second.Text.Should().Be("lo!"); });
    }

    [Fact]
    public async Task SendWithToolsAsync_StreamingToolRound_AccumulatesToolCallAcrossChunks()
    {
        var body = ToolStream("create_habit", "{\"title\":\"Read more\"}");
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("create it", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasToolCalls.Should().BeTrue();
        result.Value.ConversationContext.Should().NotBeNull();
        var toolCall = result.Value.ToolCalls!.Single();
        toolCall.Name.Should().Be("create_habit");
        toolCall.Id.Should().Be("call_1");
        toolCall.Args.GetProperty("title").GetString().Should().Be("Read more");
        sink.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task SendWithToolsAsync_ContentBeforeToolCalls_EmitsResetAfterDeltas()
    {
        var body = ToolStream("query_goals", "{}", "Checking that for you");
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("check goals", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasToolCalls.Should().BeTrue();
        sink.Events.Should().HaveCount(2);
        sink.Events[0].Kind.Should().Be(AiStreamEventKind.Delta);
        sink.Events[^1].Kind.Should().Be(AiStreamEventKind.Reset);
    }

    [Fact]
    public async Task SendWithToolsAsync_MidStreamDrop_ReturnsFailure()
    {
        var prefix = ResponsesTestData.Event(new StreamingResponseOutputTextDeltaUpdate { Delta = "Hel" });
        var (service, sink) = BuildService(new DroppingHandler(prefix));

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streamSink: sink.Handle);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiUnavailable.Message);
    }

    [Fact]
    public async Task SendWithToolsAsync_NullSink_UsesBufferedCompletion()
    {
        var completion = ResponsesTestData.Text("Hi there");
        var handler = new JsonHandler(completion);
        var (service, sink) = BuildService(handler);

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []));

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("Hi there");
        sink.Events.Should().BeEmpty();
        handler.LastRequestBody.Should().NotContain("\"stream\":true");
        handler.LastRequestBody.Should().NotContain("stream_options");
    }

    [Fact]
    public async Task SendWithToolsAsync_BufferedRound_RecordsUsageForUser()
    {
        var userId = Guid.NewGuid();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var (service, _) = BuildService(new JsonHandler(BufferedCompletion), usageRecorder);

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", [], userId));

        result.IsSuccess.Should().BeTrue();
        result.Value.ReportedTokenCount.Should().Be(18);
        await usageRecorder.Received(1).RecordAsync(
            "chat", "gpt-6-luna", 3, 11, 7, 18, Arg.Any<CancellationToken>(), userId);
    }

    [Fact]
    public async Task SendWithToolsAsync_StreamingRound_RequestsAndRecordsUsageForUser()
    {
        var userId = Guid.NewGuid();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var handler = new SseHandler(
            TextStream("Hello", "Hello"));
        var (service, sink) = BuildService(handler, usageRecorder);

        var result = await service.SendWithToolsAsync(
            new AiToolRequest("hello", "system", [], userId),
            streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.ReportedTokenCount.Should().Be(18);
        handler.LastRequestBody.Should().Contain("\"stream\":true");
        await usageRecorder.Received(1).RecordAsync(
            "chat", "gpt-6-luna", 3, 11, 7, 18, Arg.Any<CancellationToken>(), userId);
    }

    [Fact]
    public async Task SendWithToolsAsync_StreamingRoundWithoutUsage_LogsAndDoesNotRecord()
    {
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var response = ResponsesTestData.Response("Hello");
        response.Usage = null;
        var body = ResponsesTestData.Stream(response, "Hello");
        var (service, logger) = BuildServiceWithRecordingLogger(new SseHandler(body), usageRecorder);
        var sink = new CollectingSink();

        var result = await service.SendWithToolsAsync(
            new AiToolRequest("hello", "system", [], Guid.NewGuid()),
            streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        logger.WarningEventIds.Should().Contain(11);
        await usageRecorder.DidNotReceiveWithAnyArgs().RecordAsync(
            default!, default!, default, default, default, default, default, default);
    }

    [Fact]
    public async Task SendWithToolsAsync_RecorderFailure_DoesNotFailCompletedStream()
    {
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        usageRecorder.RecordAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(),
                Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .Returns(Task.FromException(new InvalidOperationException("recorder failed")));
        var body = TextStream("Hello", "Hello");
        var (service, logger) = BuildServiceWithRecordingLogger(new SseHandler(body), usageRecorder);
        var sink = new CollectingSink();

        var result = await service.SendWithToolsAsync(
            new AiToolRequest("hello", "system", [], Guid.NewGuid()),
            streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("Hello");
        sink.Events.Should().ContainSingle();
        logger.WarningEventIds.Should().Contain(12);
    }

    [Fact]
    public async Task ContinueWithToolResultsAsync_TwoRounds_RecordTwoCallsForSameUser()
    {
        var userId = Guid.NewGuid();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var handler = new SequenceSseHandler(
            ToolStream("create_habit", "{}"),
            TextStream("Done", "Done"));
        var (service, sink) = BuildService(handler, usageRecorder);

        var first = await service.SendWithToolsAsync(
            new AiToolRequest("create it", "system", [], userId),
            streamSink: sink.Handle);
        var second = await service.ContinueWithToolResultsAsync(
            first.Value.ConversationContext!,
            [new AiToolCallResult("create_habit", "call_1", true, null, null, null)],
            streamSink: sink.Handle);

        second.IsSuccess.Should().BeTrue();
        second.Value.TextMessage.Should().Be("Done");
        await usageRecorder.Received(2).RecordAsync(
            "chat", "gpt-6-luna", 3, 11, 7, 18, Arg.Any<CancellationToken>(), userId);
    }

    [Fact]
    public async Task ContinueWithToolResultsAsync_OversizedPayload_SendsValidDropMarker()
    {
        var handler = new SequenceSseHandler(
            ToolStream("list_tags", "{}"),
            TextStream("Use a narrower filter", "Use a narrower filter"));
        var (service, sink) = BuildService(handler);
        var first = await service.SendWithToolsAsync(
            new AiToolRequest("list tags", "system", [], Guid.NewGuid()),
            streamSink: sink.Handle);
        var oversizedPayload = Enumerable.Range(0, 2_000)
            .Select(index => new { id = Guid.NewGuid(), name = $"tag-{index:D4}-{new string('x', 20)}" })
            .ToList();

        var second = await service.ContinueWithToolResultsAsync(
            first.Value.ConversationContext!,
            [new AiToolCallResult("list_tags", "call_1", true, null, null, null, oversizedPayload)],
            streamSink: sink.Handle);

        second.IsSuccess.Should().BeTrue();
        handler.RequestBodies.Should().HaveCount(2);
        using var requestDocument = JsonDocument.Parse(handler.RequestBodies[1]);
        var toolMessage = requestDocument.RootElement
            .GetProperty("input")
            .EnumerateArray()
            .Single(message => message.GetProperty("type").GetString() == "function_call_output");
        var toolContent = toolMessage.GetProperty("output").GetString();
        toolContent.Should().NotBeNull();
        toolContent!.Length.Should().BeLessThan(AppConstants.MaxAiToolPayloadJsonLength);
        using var toolDocument = JsonDocument.Parse(toolContent);
        var payload = toolDocument.RootElement.GetProperty("payload");
        payload.GetProperty("dropped").GetBoolean().Should().BeTrue();
        payload.GetProperty("original_length").GetInt32().Should().BeGreaterThan(AppConstants.MaxAiToolPayloadJsonLength);
        payload.GetProperty("instruction").GetString().Should().Contain("narrower filter");
        toolContent.Should().NotContain("tag-1999");
    }

    [Fact]
    public async Task SendWithToolsAsync_BufferedLengthFinish_LogsTruncationWarningAndKeepsText()
    {
        var response = ResponsesTestData.Response("partial list");
        response.Status = ResponseStatus.Incomplete;
        response.IncompleteStatusDetails = new ResponseIncompleteStatusDetails { Reason = ResponseIncompleteStatusReason.MaxOutputTokens };
        var completion = ModelReaderWriter.Write(response).ToString();
        var (service, logger) = BuildServiceWithRecordingLogger(new JsonHandler(completion));

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []));

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("partial list");
        result.Value.IsTruncated.Should().BeTrue();
        logger.WarningEventIds.Should().Contain(8);
    }

    [Fact]
    public async Task SendWithToolsAsync_StreamingLengthFinish_LogsTruncationWarningAndKeepsText()
    {
        var response = ResponsesTestData.Response("partial");
        response.Status = ResponseStatus.Incomplete;
        response.IncompleteStatusDetails = new ResponseIncompleteStatusDetails { Reason = ResponseIncompleteStatusReason.MaxOutputTokens };
        var body = ResponsesTestData.Stream(response, "partial");
        var (service, logger) = BuildServiceWithRecordingLogger(new SseHandler(body));
        var sink = new CollectingSink();

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("partial");
        result.Value.IsTruncated.Should().BeTrue();
        logger.WarningEventIds.Should().Contain(8);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SendWithToolsAsync_LengthFinishWithUnfinishedTool_PreservesPartialStatusWithoutParsingArguments(
        bool streaming, bool hasText)
    {
        var response = ResponsesTestData.Response(hasText ? "Partial reply" : null);
        response.Status = ResponseStatus.Incomplete;
        response.IncompleteStatusDetails = new ResponseIncompleteStatusDetails { Reason = ResponseIncompleteStatusReason.MaxOutputTokens };
        response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("cut_off", "bulk_update_habits", BinaryData.FromString("{\"filter\":")));
        HttpMessageHandler handler = streaming
            ? new SseHandler(ResponsesTestData.Stream(response))
            : new JsonHandler(ModelReaderWriter.Write(response).ToString());
        var (service, sink) = BuildService(handler);

        var result = await service.SendWithToolsAsync(new AiToolRequest("change all descriptions", "system", []),
            streamSink: streaming ? sink.Handle : null);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsTruncated.Should().BeTrue();
        result.Value.TextMessage.Should().Be(hasText ? "Partial reply" : string.Empty);
        result.Value.HasToolCalls.Should().BeFalse();
        result.Value.ReportedTokenCount.Should().Be(3);
    }

    [Fact]
    public async Task SendWithToolsAsync_WithUserId_SetsSafetyIdentifierAndCacheKey()
    {
        var handler = new JsonHandler(BufferedCompletion);
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiIntentService(aiClient, usageRecorder, NullLogger<AiIntentService>.Instance);
        var userId = Guid.NewGuid();

        await service.SendWithToolsAsync(new AiToolRequest("hello", "system", [], userId));

        handler.LastRequestBody.Should().Contain(userId.ToString("N"));
        using var requestDocument = JsonDocument.Parse(handler.LastRequestBody!);
        requestDocument.RootElement.GetProperty("prompt_cache_key").GetString().Should().Be(userId.ToString("N"));
        requestDocument.RootElement.GetProperty("safety_identifier").GetString().Should().Be(userId.ToString("N"));
        requestDocument.RootElement.TryGetProperty("user", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SendWithToolsAsync_HistoryWithinWindow_DoesNotSummarize()
    {
        var handler = new CountingJsonHandler(BufferedCompletion);
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiIntentService(aiClient, usageRecorder, NullLogger<AiIntentService>.Instance);

        await service.SendWithToolsAsync(new AiToolRequest("hello", "system", [], Guid.NewGuid(), History: BuildHistory(40)));

        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task SendWithToolsAsync_HistoryAboveControllerLimit_DoesNotIssueSummaryCall()
    {
        var handler = new CountingJsonHandler(BufferedCompletion);
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiIntentService(aiClient, usageRecorder, NullLogger<AiIntentService>.Instance);

        await service.SendWithToolsAsync(new AiToolRequest("hello", "system", [], Guid.NewGuid(), History: BuildHistory(50)));

        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task SendWithToolsAsync_CacheKey_IsStableForOneUserAndDiffersBetweenUsers()
    {
        var handler = new JsonHandler(BufferedCompletion);
        var (service, _) = BuildService(handler);
        var userId = Guid.NewGuid();
        var keys = new List<string?>();
        foreach (var id in new[] { userId, userId, Guid.NewGuid() })
        {
            await service.SendWithToolsAsync(new AiToolRequest("hello", "system", [], id));
            using var document = JsonDocument.Parse(handler.LastRequestBody!);
            keys.Add(document.RootElement.GetProperty("prompt_cache_key").GetString());
        }
        keys[0].Should().Be(userId.ToString("N"));
        keys[1].Should().Be(keys[0]);
        keys[2].Should().NotBe(keys[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinueWithToolResultsAsync_PreservesReasoningEveryCallAndCallIds(bool streaming)
    {
        var firstResponse = ResponsesTestData.Response(inputTokens: 11, outputTokens: 7, cachedTokens: 3);
        var reasoning = ResponseItem.CreateReasoningItem(Array.Empty<ReasoningSummaryPart>());
        reasoning.Id = "rs_1";
        reasoning.EncryptedContent = "encrypted-reasoning";
        firstResponse.OutputItems.Add(reasoning);
        var firstCall = ResponseItem.CreateFunctionCallItem("call_1", "query_habits", BinaryData.FromString("{}"));
        firstCall.Id = "fc_1";
        var secondCall = ResponseItem.CreateFunctionCallItem("call_2", "query_goals", BinaryData.FromString("{}"));
        secondCall.Id = "fc_2";
        firstResponse.OutputItems.Add(firstCall);
        firstResponse.OutputItems.Add(secondCall);
        var handler = new SequenceSseHandler(
            streaming ? ResponsesTestData.Stream(firstResponse) : ModelReaderWriter.Write(firstResponse).ToString(),
            streaming ? TextStream("Done", "Done") : ResponsesTestData.Text("Done"));
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            usage);
        var service = new AiIntentService(client, usage, NullLogger<AiIntentService>.Instance);
        Func<AiStreamEvent, Task>? sink = streaming ? _ => Task.CompletedTask : null;
        var userId = Guid.NewGuid();
        var declaration = new { name = "query_habits", description = "Read habits", parameters = new { type = "object", properties = new { } } };

        var first = await service.SendWithToolsAsync(new AiToolRequest("look up my habits and goals", "system", [declaration], userId), sink);
        first.IsSuccess.Should().BeTrue();
        first.Value.ToolCalls!.Select(call => call.Id).Should().Equal("call_1", "call_2");
        var second = await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!,
            [new AiToolCallResult("query_habits", "call_1", true, null, null, null),
             new AiToolCallResult("query_goals", "call_2", true, null, null, null)], sink);

        second.IsSuccess.Should().BeTrue();
        foreach (var body in handler.RequestBodies)
        {
            using var request = JsonDocument.Parse(body);
            request.RootElement.GetProperty("model").GetString().Should().Be("gpt-6-luna");
            request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        }
        using var document = JsonDocument.Parse(handler.RequestBodies[1]);
        var root = document.RootElement;
        root.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        root.TryGetProperty("temperature", out _).Should().BeFalse();
        root.GetProperty("prompt_cache_key").GetString().Should().Be(userId.ToString("N"));
        root.GetProperty("safety_identifier").GetString().Should().Be(userId.ToString("N"));
        root.TryGetProperty("user", out _).Should().BeFalse();
        root.GetProperty("tools")[0].GetProperty("name").GetString().Should().Be("query_habits");
        var items = root.GetProperty("input").EnumerateArray().ToArray();
        items.Single(item => item.GetProperty("type").GetString() == "reasoning")
            .GetProperty("encrypted_content").GetString().Should().Be("encrypted-reasoning");
        items.Where(item => item.GetProperty("type").GetString() == "function_call_output")
            .Select(item => item.GetProperty("call_id").GetString()).Should().Equal("call_1", "call_2");
        items.Count(item => item.GetProperty("type").GetString() == "function_call").Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendWithToolsAsync_DefaultModels_UseShippedPrimary(bool streaming)
    {
        var handler = new SequenceSseHandler(streaming ? TextStream("Done", "Done") : ResponsesTestData.Text("Done"));
        var usage = Substitute.For<IAiUsageRecorder>();
        var settings = new AiSettings { ApiKey = "test-key", BaseUrl = "https://orbit.test/v1" };
        var client = new AiCompletionClient(Options.Create(settings), NullLogger<AiCompletionClient>.Instance,
            usage, new HttpClientPipelineTransport(new HttpClient(handler)));
        var service = new AiIntentService(client, usage, NullLogger<AiIntentService>.Instance);
        var declaration = new { name = "query_habits", parameters = new { type = "object", properties = new { } } };

        var result = await service.SendWithToolsAsync(new AiToolRequest("look up my habits", "system", [declaration]),
            streaming ? _ => Task.CompletedTask : null);

        result.IsSuccess.Should().BeTrue();
        using var document = JsonDocument.Parse(handler.RequestBodies.Single());
        document.RootElement.GetProperty("tools")[0].GetProperty("name").GetString().Should().Be("query_habits");
        document.RootElement.GetProperty("model").GetString().Should().Be("gpt-6-luna");
        document.RootElement.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        document.RootElement.TryGetProperty("temperature", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinueWithToolResultsAsync_LegacyModel_OmitsReasoningAndKeepsTools(bool streaming)
    {
        var firstResponse = ResponsesTestData.Response();
        firstResponse.OutputItems.Add(ResponseItem.CreateFunctionCallItem("call_1", "query_habits", BinaryData.FromString("{}")));
        var handler = new SequenceSseHandler(
            streaming ? ResponsesTestData.Stream(firstResponse) : ModelReaderWriter.Write(firstResponse).ToString(),
            streaming ? TextStream("Done", "Done") : ResponsesTestData.Text("Done"));
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            usage, primaryModel: "gpt-4.1-mini");
        var service = new AiIntentService(client, usage, NullLogger<AiIntentService>.Instance);
        var declaration = new { name = "query_habits", parameters = new { type = "object", properties = new { } } };
        Func<AiStreamEvent, Task>? sink = streaming ? _ => Task.CompletedTask : null;

        var first = await service.SendWithToolsAsync(new AiToolRequest("look up my habits", "system", [declaration]), sink);
        first.IsSuccess.Should().BeTrue();
        var second = await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!,
            [new AiToolCallResult("query_habits", "call_1", true, null, null, null)], sink);

        second.IsSuccess.Should().BeTrue();
        foreach (var body in handler.RequestBodies)
        {
            using var document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("model").GetString().Should().Be("gpt-4.1-mini");
            document.RootElement.GetProperty("temperature").GetDouble().Should().BeApproximately(0.1, 0.00001);
            document.RootElement.TryGetProperty("include", out _).Should().BeFalse();
            document.RootElement.TryGetProperty("reasoning", out _).Should().BeFalse();
            document.RootElement.GetProperty("tools")[0].GetProperty("name").GetString().Should().Be("query_habits");
            document.RootElement.GetProperty("input").EnumerateArray()
                .Should().NotContain(item => item.GetProperty("type").GetString() == "reasoning");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendWithToolsAsync_MultipleAssistantMessages_PreservesBoundariesInReplyAndDeltas(bool streaming)
    {
        var response = ResponsesTestData.Response("Checking.");
        response.OutputItems.Add(ResponseItem.CreateAssistantMessageItem("Done."));
        var body = streaming
            ? ResponsesTestData.Event(new StreamingResponseOutputTextDeltaUpdate { Delta = "Checking.", ItemId = "msg_1", OutputIndex = 0 })
                + ResponsesTestData.Event(new StreamingResponseOutputTextDeltaUpdate { Delta = "Done.", ItemId = "msg_2", OutputIndex = 1 })
                + ResponsesTestData.Stream(response)
            : ModelReaderWriter.Write(response).ToString();
        var (service, sink) = BuildService(streaming ? new SseHandler(body) : new JsonHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streaming ? sink.Handle : null);

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("Checking.\n\nDone.");
        if (streaming)
            string.Concat(sink.Events.Select(update => update.Text)).Should().Be(result.Value.TextMessage);
    }

    [Fact]
    public async Task SendWithToolsAsync_StreamEndsWithoutTerminalEvent_ReturnsFailure()
    {
        var body = ResponsesTestData.Event(new StreamingResponseOutputTextDeltaUpdate { Delta = "partial" });
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), sink.Handle);

        result.IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendWithToolsAsync_FailedResponse_DoesNotAcceptPartialText(bool streaming)
    {
        var response = ResponsesTestData.Response("partial");
        response.Status = ResponseStatus.Failed;
        var body = streaming
            ? ResponsesTestData.Event(new StreamingResponseFailedUpdate { Response = response })
            : ModelReaderWriter.Write(response).ToString();
        var (service, sink) = BuildService(streaming ? new SseHandler(body) : new JsonHandler(body));

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streaming ? sink.Handle : null);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task SendWithToolsAsync_Image_SendsResponsesImageContent()
    {
        var handler = new JsonHandler(BufferedCompletion);
        var (service, _) = BuildService(handler);

        var result = await service.SendWithToolsAsync(new AiToolRequest("describe", "system", [],
            ImageData: [1, 2, 3], ImageMimeType: "image/png"));

        result.IsSuccess.Should().BeTrue();
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var parts = document.RootElement.GetProperty("input")[1].GetProperty("content");
        parts[0].GetProperty("text").GetString().Should().Be("describe");
        parts[1].GetProperty("type").GetString().Should().Be("input_image");
        parts[1].GetProperty("image_url").GetString().Should().Be("data:image/png;base64,AQID");
    }

    private static readonly string BufferedCompletion = ResponsesTestData.Text("Hi there", 11, 7, 3);

    private static List<ChatHistoryMessage> BuildHistory(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ChatHistoryMessage(i % 2 == 0 ? "user" : "assistant", $"message {i}"))
            .ToList();

    [Fact]
    public async Task SendWithToolsAsync_Streaming_RequestsResponsesStream()
    {
        var handler = new SseHandler(TextStream("hi", "hi"));
        var (service, sink) = BuildService(handler);

        var result = await service.SendWithToolsAsync(new AiToolRequest("hello", "system", []), streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        using var request = JsonDocument.Parse(handler.LastRequestBody!);
        request.RootElement.GetProperty("stream").GetBoolean().Should().BeTrue();
        request.RootElement.TryGetProperty("stream_options", out _).Should().BeFalse();
    }

    private static (AiIntentService Service, CollectingSink Sink) BuildService(
        HttpMessageHandler handler,
        IAiUsageRecorder? usageRecorder = null)
    {
        usageRecorder ??= Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiIntentService(aiClient, usageRecorder, NullLogger<AiIntentService>.Instance);
        return (service, new CollectingSink());
    }

    private static (AiIntentService Service, RecordingLogger Logger) BuildServiceWithRecordingLogger(
        HttpMessageHandler handler,
        IAiUsageRecorder? usageRecorder = null)
    {
        usageRecorder ??= Substitute.For<IAiUsageRecorder>();
        var aiClient = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var logger = new RecordingLogger();
        var service = new AiIntentService(aiClient, usageRecorder, logger);
        return (service, logger);
    }

    private static ResponsesClient BuildResponsesClient(HttpMessageHandler handler) =>
        new(
            credential: new ApiKeyCredential("test-key"),
            options: new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            });

    private static string TextStream(string text, params string[] deltas)
        => ResponsesTestData.Stream(ResponsesTestData.Response(text, 11, 7, 3), deltas);

    private static string ToolStream(string name, string arguments, string? text = null)
    {
        var response = ResponsesTestData.Response(text, 11, 7, 3);
        var call = ResponseItem.CreateFunctionCallItem("call_1", name, BinaryData.FromString(arguments));
        call.Id = "fc_1";
        response.OutputItems.Add(call);
        return ResponsesTestData.Event(new StreamingResponseOutputItemAddedUpdate
        {
            Item = ResponseItem.CreateFunctionCallItem("call_1", name, BinaryData.FromString("")),
            OutputIndex = 0
        }) + ResponsesTestData.Event(new StreamingResponseFunctionCallArgumentsDeltaUpdate
        {
            ItemId = "fc_1",
            OutputIndex = 0,
            Delta = BinaryData.FromString(arguments[..(arguments.Length / 2)])
        }) + ResponsesTestData.Event(new StreamingResponseFunctionCallArgumentsDeltaUpdate
        {
            ItemId = "fc_1",
            OutputIndex = 0,
            Delta = BinaryData.FromString(arguments[(arguments.Length / 2)..])
        }) + ResponsesTestData.Stream(response, text is null ? [] : [text]);
    }

    private sealed class CollectingSink
    {
        public List<AiStreamEvent> Events { get; } = [];

        public Task Handle(AiStreamEvent streamEvent)
        {
            Events.Add(streamEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<AiIntentService>
    {
        public List<int> WarningEventIds { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                WarningEventIds.Add(eventId.Id);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return BuildResponse(request);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return BuildResponse(request);
        }

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(body, Encoding.UTF8, "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class SequenceSseHandler(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);
        public List<string> RequestBodies { get; } = [];

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                RequestBodies.Add(request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            return BuildResponse(request);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return BuildResponse(request);
        }

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var body = _bodies.Dequeue();
            var content = new StringContent(body, Encoding.UTF8, body.StartsWith("data:", StringComparison.Ordinal) ? "text/event-stream" : "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return BuildResponse(request);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return BuildResponse(request);
        }

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class CountingJsonHandler(string body) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Build(request);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(Build(request));
        }

        private HttpResponseMessage Build(HttpRequestMessage request)
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class DroppingHandler(string prefix) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => BuildResponse(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(BuildResponse(request));

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StreamContent(new DroppingStream(Encoding.UTF8.GetBytes(prefix)));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class DroppingStream(byte[] prefix) : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served)
                throw new IOException("connection reset");

            _served = true;
            var copied = Math.Min(count, prefix.Length);
            Array.Copy(prefix, 0, buffer, offset, copied);
            return copied;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
