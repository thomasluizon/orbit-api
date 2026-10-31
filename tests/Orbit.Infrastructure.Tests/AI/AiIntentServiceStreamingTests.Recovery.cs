#pragma warning disable OPENAI001

using System.Net;
using System.Text;
using FluentAssertions;
using OpenAI.Responses;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.AI;

public partial class AiIntentServiceStreamingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfferContinuation_WhitespaceRunCancelsAt257AndRetriesOnce(bool exhausted)
    {
        var started = ResponsesTestData.Response();
        started.Id = "resp_whitespace";
        started.Status = ResponseStatus.InProgress;
        started.Usage = null;
        string Delta(string value) => ResponsesTestData.Event(new StreamingResponseFunctionCallArgumentsDeltaUpdate
        {
            ItemId = "fc_ask",
            OutputIndex = 0,
            Delta = BinaryData.FromString(value)
        });
        string[] chunks =
        [
            ResponsesTestData.Event(new StreamingResponseCreatedUpdate { Response = started }),
            ResponsesTestData.Event(new StreamingResponseOutputItemAddedUpdate
            {
                OutputIndex = 0, Item = ResponseItem.CreateFunctionCallItem("discarded", "ask", BinaryData.FromString(""))
            }),
            Delta("{\"input\":{\"title\":\"private value\","),
            Delta(new string(' ', 128)), Delta(new string('\t', 128)), Delta("\r"),
        ];
        var handler = new GuardedRecoveryHandler(chunks, exhausted);
        var (service, logger) = BuildServiceWithRecordingLogger(handler);
        var sink = new CollectingSink();
        var read = await service.SendWithToolsAsync(new AiToolRequest("Prepare it.", "system", []), sink.Handle);

        var result = await service.ContinueWithToolResultsAsync(read.Value.ConversationContext!,
            [new("query_habits", "call_1", true, null, null, null)], sink.Handle);

        result.IsSuccess.Should().BeTrue(result.Error + string.Join("\n", logger.Exceptions));
        handler.RequestBodies.Should().HaveCount(3);
        handler.RequestBodies[2].Should().Be(handler.RequestBodies[1]);
        handler.Streams.Should().HaveCount(exhausted ? 2 : 1);
        handler.Streams.Should().OnlyContain(stream => stream.ChunksRead == chunks.Length && stream.Disposed);
        result.Value.IsTruncated.Should().Be(exhausted);
        (result.Value.ToolCalls ?? []).Should().NotContain(call => call.Id == "discarded");
        if (!exhausted) result.Value.ToolCalls.Should().ContainSingle(call => call.Name == "create_habit");
        ((List<ResponseItem>)result.Value.ConversationContext!.Messages).OfType<FunctionCallResponseItem>()
            .Should().NotContain(call => call.CallId == "discarded");
        logger.WarningProperties.Should().Contain(entry =>
            entry.Properties.ContainsKey("ResponseId") && (string?)entry.Properties["ResponseId"] == "resp_whitespace"
            && entry.Properties.ContainsKey("ToolName") && (string?)entry.Properties["ToolName"] == "ask");
        logger.WarningProperties.SelectMany(entry => entry.Properties.Values).OfType<string>()
            .Should().NotContain(value => value.Contains("private value", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(256, false)]
    [InlineData(600, true)]
    public async Task OfferContinuation_JsonWhitespaceLimitAllowsBoundaryAndStringContent(int length, bool inString)
    {
        var arguments = inString
            ? "{\"title\":\"escaped \\\"" + new string(' ', length) + "\\\\\",\"description\":\"valid\"}"
            : "{\"title\":" + new string(' ', length) + "\"valid\"}";
        var response = ResponsesTestData.Response();
        response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("valid", "create_habit", BinaryData.FromString(arguments)));
        var chunks = arguments.Select(character => ResponsesTestData.Event(new StreamingResponseFunctionCallArgumentsDeltaUpdate
        {
            ItemId = "fc_valid",
            OutputIndex = 0,
            Delta = BinaryData.FromString(character.ToString())
        }));
        var handler = new SequenceSseHandler(string.Concat(chunks) + ResponsesTestData.Stream(response));
        var (service, sink) = BuildService(handler);

        var result = await service.SendWithToolsAsync(new AiToolRequest("Prepare it.", "system", []), sink.Handle);

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.ToolCalls.Should().ContainSingle(call => call.Id == "valid");
        handler.RequestBodies.Should().ContainSingle();
    }

    private sealed class GuardedRecoveryHandler(string[] chunks, bool exhausted) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];
        public List<GuardedEventStream> Streams { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            HttpContent content;
            if (RequestBodies.Count == 2 || RequestBodies.Count == 3 && exhausted)
            {
                var stream = new GuardedEventStream(chunks);
                Streams.Add(stream);
                content = new StreamContent(stream);
                content.Headers.ContentType = new("text/event-stream");
            }
            else content = new StringContent(RequestBodies.Count == 1 ? ToolStream("query_habits", "{}")
                : ToolStream("create_habit", "{\"title\":\"Correct\"}"), Encoding.UTF8, "text/event-stream");
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }

    private sealed class GuardedEventStream(string[] chunks) : Stream
    {
        private int _offset;
        public int ChunksRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadChunk(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadChunk(buffer.Span));
        private int ReadChunk(Span<byte> buffer)
        {
            if (ChunksRead == chunks.Length) throw new IOException("The whitespace guard must stop before reading the next event.");
            var bytes = Encoding.UTF8.GetBytes(chunks[ChunksRead]);
            var count = Math.Min(buffer.Length, bytes.Length - _offset);
            bytes.AsSpan(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset == bytes.Length) { ChunksRead++; _offset = 0; }
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
