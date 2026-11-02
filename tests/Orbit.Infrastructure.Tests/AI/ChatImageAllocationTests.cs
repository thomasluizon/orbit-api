#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Reflection;
using System.Text;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Api.Controllers;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

[CollectionDefinition("Image allocation", DisableParallelization = true)]
public class ImageAllocationCollection;

[Collection("Image allocation")]
public class ChatImageAllocationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenMiBUploadAndModelRequest_AllocateOnlyUploadAndEncodedImageBuffers(bool streaming)
    {
        var image = new byte[10 * 1024 * 1024];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(image, 0);
        var controller = new ChatController(Substitute.For<IMediator>(), new ImageValidationService(), NullLogger<ChatController>.Instance);
        var process = typeof(ChatController).GetMethod("ProcessImageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var transport = new DiscardingRequestHandler(streaming);
        var sdk = new ResponsesClient(new System.ClientModel.ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(transport))
        });
        var client = new AiCompletionClient(sdk, NullLogger<AiCompletionClient>.Instance, Substitute.For<Orbit.Domain.Interfaces.IAiUsageRecorder>());
        var service = new AiIntentService(AiBudgetTestDouble.Wrap(client), Substitute.For<Orbit.Domain.Interfaces.IAiUsageRecorder>(), NullLogger<AiIntentService>.Instance);
        var smallFile = new FormFile(new MemoryStream(image[..8]), 0, 8, "image", "image.png");
        await Process(smallFile);
        var warm = await service.SendWithToolsAsync(new("Warm", "system", []), streaming ? _ => Task.CompletedTask : null);
        warm.IsSuccess.Should().BeTrue(warm.Error);
        var file = new FormFile(new MemoryStream(image, writable: false), 0, image.Length, "image", "image.png");
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var parsed = await Process(file);
        parsed.Error.Should().BeNull();
        var result = await service.SendWithToolsAsync(new AiToolRequest("Inspect", "system", [],
            ImageData: parsed.Data, ImageMimeType: parsed.MimeType), streaming ? _ => Task.CompletedTask : null);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        result.IsSuccess.Should().BeTrue(result.Error);
        output.WriteLine($"Allocated bytes for streaming={streaming}: {allocated}");
        allocated.Should().BeLessThan(image.LongLength + ((image.LongLength + 2) / 3 * 4) + 1_000_000);
        transport.Bytes.Should().BeGreaterThan(image.LongLength);

        Task<(byte[]? Data, string? MimeType, IActionResult? Error)> Process(IFormFile form) =>
            (Task<(byte[]?, string?, IActionResult?)>)process.Invoke(controller, [form, CancellationToken.None])!;
    }

    [Fact]
    public async Task ThreeImageRounds_ReusePayloadAndKeepLaterAllocationsSmall()
    {
        var bytes = new byte[10 * 1024 * 1024];
        using var transport = new RepeatedImageHandler();
        var sdk = new ResponsesClient(new System.ClientModel.ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            Transport = new HttpClientPipelineTransport(new HttpClient(transport))
        });
        var client = new AiCompletionClient(sdk, NullLogger<AiCompletionClient>.Instance,
            Substitute.For<Orbit.Domain.Interfaces.IAiUsageRecorder>());
        var service = new AiIntentService(AiBudgetTestDouble.Wrap(client),
            Substitute.For<Orbit.Domain.Interfaces.IAiUsageRecorder>(), NullLogger<AiIntentService>.Instance);
        var first = await service.SendWithToolsAsync(new("Inspect", "system", [new { name = "query_habits" }],
            ImageData: bytes, ImageMimeType: "image/png"));
        first.IsSuccess.Should().BeTrue(first.Error);
        transport.Lengths.Single().Should().BeGreaterThan(bytes.Length);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var current = first;
        for (var index = 0; index < 2; index++)
        {
            current = await service.ContinueWithToolResultsAsync(current.Value.ConversationContext!,
                [new("query_habits", "call", true, null, null, null)]);
            current.IsSuccess.Should().BeTrue(current.Error);
        }
        var laterAllocations = GC.GetTotalAllocatedBytes(precise: true) - before;
        transport.Lengths.Should().HaveCount(3).And.OnlyContain(length => length > bytes.LongLength);
        laterAllocations.Should().BeLessThan(bytes.LongLength / 2);
    }

    private sealed class RepeatedImageHandler : HttpMessageHandler
    {
        internal List<long> Lengths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var destination = new CountingSink();
            await request.Content!.CopyToAsync(destination, ct);
            Lengths.Add(destination.Bytes);
            var response = ResponsesTestData.Response();
            response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("call", "query_habits", BinaryData.FromString("{}")));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(ModelReaderWriter.Write(response).ToString(), Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public void LegacyTenMiBDataUri_IsAcceptedByTheInstalledRuntime()
    {
        var uri = "data:image/png;base64," + Convert.ToBase64String(new byte[10 * 1024 * 1024]);
        new Uri(uri).OriginalString.Should().Be(uri);
    }

    private sealed class DiscardingRequestHandler(bool streaming) : HttpMessageHandler
    {
        public long Bytes { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var destination = new CountingSink();
            await request.Content!.CopyToAsync(destination, cancellationToken);
            Bytes = destination.Bytes;
            var body = streaming ? ResponsesTestData.Stream(ResponsesTestData.Response("Done")) : ResponsesTestData.Text("Done");
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
        }
    }

    private sealed class CountingSink : Stream
    {
        public long Bytes { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Bytes += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Bytes += buffer.Length;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Bytes += buffer.Length; return ValueTask.CompletedTask; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
