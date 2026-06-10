using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI;
using OpenAI.Chat;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public class AiIntentServiceStreamingTests
{
    [Fact]
    public async Task SendWithToolsAsync_StreamingTextRound_EmitsDeltasAndReturnsFullText()
    {
        var body = RoleChunk() + ContentChunk("Hel") + ContentChunk("lo!") + FinishChunk("stop") + Done();
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync("hello", "system", [], streamSink: sink.Handle);

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
        var body = RoleChunk()
            + ToolCallStartChunk(0, "call_1", "create_habit")
            + ToolCallArgsChunk(0, """{"title":""")
            + ToolCallArgsChunk(0, """ "Read more"}""")
            + FinishChunk("tool_calls")
            + Done();
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync("create it", "system", [], streamSink: sink.Handle);

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
        var body = RoleChunk()
            + ContentChunk("Checking that for you")
            + ToolCallStartChunk(0, "call_1", "query_goals")
            + ToolCallArgsChunk(0, "{}")
            + FinishChunk("tool_calls")
            + Done();
        var (service, sink) = BuildService(new SseHandler(body));

        var result = await service.SendWithToolsAsync("check goals", "system", [], streamSink: sink.Handle);

        result.IsSuccess.Should().BeTrue();
        result.Value.HasToolCalls.Should().BeTrue();
        sink.Events.Should().HaveCount(2);
        sink.Events[0].Kind.Should().Be(AiStreamEventKind.Delta);
        sink.Events[^1].Kind.Should().Be(AiStreamEventKind.Reset);
    }

    [Fact]
    public async Task SendWithToolsAsync_MidStreamDrop_ReturnsFailure()
    {
        var prefix = RoleChunk() + ContentChunk("Hel");
        var (service, sink) = BuildService(new DroppingHandler(prefix));

        var result = await service.SendWithToolsAsync("hello", "system", [], streamSink: sink.Handle);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("AI service temporarily unavailable");
    }

    [Fact]
    public async Task SendWithToolsAsync_NullSink_UsesBufferedCompletion()
    {
        const string completion = """
            {"id":"chatcmpl-test","object":"chat.completion","created":1700000000,"model":"gpt-test",
             "choices":[{"index":0,"message":{"role":"assistant","content":"Hi there"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """;
        var handler = new JsonHandler(completion);
        var (service, sink) = BuildService(handler);

        var result = await service.SendWithToolsAsync("hello", "system", []);

        result.IsSuccess.Should().BeTrue();
        result.Value.TextMessage.Should().Be("Hi there");
        sink.Events.Should().BeEmpty();
        handler.LastRequestBody.Should().NotContain("\"stream\":true");
    }

    private static (AiIntentService Service, CollectingSink Sink) BuildService(HttpMessageHandler handler)
    {
        var chatClient = new ChatClient(
            model: "gpt-test",
            credential: new ApiKeyCredential("test-key"),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            });

        var aiClient = new AiCompletionClient(chatClient, NullLogger<AiCompletionClient>.Instance);
        var service = new AiIntentService(aiClient, NullLogger<AiIntentService>.Instance);
        return (service, new CollectingSink());
    }

    private static string Chunk(string deltaJson, string finishReason = "null")
    {
        return "data: {\"id\":\"chatcmpl-test\",\"object\":\"chat.completion.chunk\",\"created\":1700000000," +
               $"\"model\":\"gpt-test\",\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{finishReason}}}]}}\n\n";
    }

    private static string RoleChunk() => Chunk("""{"role":"assistant","content":""}""");

    private static string ContentChunk(string text) => Chunk($"{{\"content\":{JsonSerializer.Serialize(text)}}}");

    private static string ToolCallStartChunk(int index, string id, string name)
    {
        return Chunk($"{{\"tool_calls\":[{{\"index\":{index},\"id\":\"{id}\",\"type\":\"function\"," +
                     $"\"function\":{{\"name\":\"{name}\",\"arguments\":\"\"}}}}]}}");
    }

    private static string ToolCallArgsChunk(int index, string argsFragment)
    {
        return Chunk($"{{\"tool_calls\":[{{\"index\":{index}," +
                     $"\"function\":{{\"arguments\":{JsonSerializer.Serialize(argsFragment)}}}}}]}}");
    }

    private static string FinishChunk(string reason) => Chunk("{}", $"\"{reason}\"");

    private static string Done() => "data: [DONE]\n\n";

    private sealed class CollectingSink
    {
        public List<AiStreamEvent> Events { get; } = [];

        public Task Handle(AiStreamEvent streamEvent)
        {
            Events.Add(streamEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class SseHandler(string body) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => BuildResponse(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(BuildResponse(request));

        private HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(body, Encoding.UTF8, "text/event-stream");
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
