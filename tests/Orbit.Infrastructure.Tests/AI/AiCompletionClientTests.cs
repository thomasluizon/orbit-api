#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;
using Orbit.Domain.Models;

namespace Orbit.Infrastructure.Tests.AI;

public class AiCompletionClientTests
{
    [Fact]
    public async Task CompleteJsonAsync_SubTaskTier_OmitsTemperature()
    {
        var handler = new CapturingHandler();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);

        await client.CompleteJsonAsync<Probe>("You are a helpful assistant. Respond only with valid JSON.", "extract facts", purpose: "fact_extraction", tier: AiModelTier.SubTask);

        handler.LastRequestBody.Should().NotBeNull();
        handler.LastRequestUri.Should().EndWith("/responses");
        handler.LastRequestBody.Should().NotContain("temperature");
        await usageRecorder.Received(1).RecordAsync(
            "fact_extraction", "gpt-6-luna", 0, 1, 2, 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteJsonAsync_LegacyPrimaryModel_SendsTemperature()
    {
        var handler = new CapturingHandler();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());

        await client.CompleteJsonAsync<Probe>("You are a helpful assistant. Respond only with valid JSON.", "extract facts", temperature: 0.1, purpose: "json", tier: AiModelTier.Primary);

        handler.LastRequestBody.Should().Contain("temperature");
    }

    [Fact]
    public async Task CompleteJsonAsync_WithUserId_AttributesRecordedUsage()
    {
        var handler = new CapturingHandler();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var userId = Guid.NewGuid();

        await client.CompleteJsonAsync<Probe>("system", "user", userId: userId);

        await usageRecorder.Received(1).RecordAsync(
            "json", "gpt-4.1-mini", 0, 1, 2, 3, Arg.Any<CancellationToken>(), userId);
    }

    [Fact]
    public void ResolveSubTaskModel_EmptyConfig_FallsBackToPrimary()
    {
        AiCompletionClient.ResolveSubTaskModel("", "gpt-4.1-mini").Should().Be("gpt-4.1-mini");
        AiCompletionClient.ResolveSubTaskModel("   ", "gpt-4.1-mini").Should().Be("gpt-4.1-mini");
    }

    [Fact]
    public void ResolveSubTaskModel_ConfiguredModel_IsUsed()
    {
        AiCompletionClient.ResolveSubTaskModel("gpt-5.4-nano", "gpt-4.1-mini").Should().Be("gpt-5.4-nano");
    }

    [Theory]
    [InlineData(AiModelTier.Primary, "gpt-6.1-sol", "gpt-6-luna", false)]
    [InlineData(AiModelTier.Primary, "gpt-5.4-nano", "gpt-6-luna", false)]
    [InlineData(AiModelTier.SubTask, "gpt-6.1-sol", "gpt-4.1-mini", true)]
    [InlineData(AiModelTier.Primary, "gpt-4.1-mini", "gpt-5.4-nano", true)]
    [InlineData(AiModelTier.SubTask, "gpt-4.1-mini", "gpt-5.4-nano", false)]
    [InlineData(AiModelTier.SubTask, "gpt-4.1-mini", "gpt-4.1-mini", true)]
    public void ShouldApplyTemperature_UsesSelectedModelCapabilities(
        AiModelTier tier, string primary, string subTask, bool expected)
        => AiCompletionClient.ShouldApplyTemperature(tier, primary, subTask).Should().Be(expected);

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task CompletionDeadline_CancelsTheWholeCallIncludingRetryWaits(string kind)
    {
        for (var iteration = 0; iteration < (kind == "json" ? 200 : 1); iteration++)
        {
            var client = new AiCompletionClient(BuildResponsesClient(new WaitingHandler()), NullLogger<AiCompletionClient>.Instance,
                Substitute.For<IAiUsageRecorder>(), TimeSpan.FromMilliseconds(50));
            var intent = new AiIntentService(client, Substitute.For<IAiUsageRecorder>(), NullLogger<AiIntentService>.Instance);
            Func<Task> call = kind switch
            {
                "json" => async () => await client.CompleteJsonAsync<Probe>("system", "user"),
                "text" => async () => await client.CompleteTextAsync("system", "user"),
                _ => async () => await intent.SendWithToolsAsync(new AiToolRequest("user", "system", [], Guid.NewGuid()),
                    kind == "stream" ? _ => Task.CompletedTask : null)
            };

            Func<Task> boundedCall = () => call().WaitAsync(TimeSpan.FromSeconds(5));
            await boundedCall.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Theory]
    [InlineData("gpt-6.1-sol", "gpt-6-luna", AiModelTier.Primary, "gpt-6.1-sol", false)]
    [InlineData("gpt-6.1-sol", "gpt-6-luna", AiModelTier.SubTask, "gpt-6-luna", false)]
    [InlineData("gpt-4.1-mini", "gpt-6-luna", AiModelTier.Primary, "gpt-4.1-mini", true)]
    [InlineData("gpt-6.1-sol", "gpt-4.1-mini", AiModelTier.SubTask, "gpt-4.1-mini", true)]
    [InlineData("gpt-4.1-mini", "", AiModelTier.SubTask, "gpt-4.1-mini", true)]
    public async Task CompleteTextAsync_ConfiguredTier_SendsCompatibleResponsesOptions(
        string primary, string subTask, AiModelTier tier, string expectedModel, bool temperatureAccepted)
    {
        var handler = new CapturingHandler();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>(), primaryModel: primary, subTaskModel: subTask);

        await client.CompleteTextAsync("system", "user", tier: tier);

        using var document = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var root = document.RootElement;
        root.GetProperty("model").GetString().Should().Be(expectedModel);
        root.GetProperty("store").GetBoolean().Should().BeFalse();
        root.TryGetProperty("temperature", out _).Should().Be(temperatureAccepted);
        if (temperatureAccepted)
            root.TryGetProperty("reasoning", out _).Should().BeFalse();
        else
            root.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
    }

    [Fact]
    public async Task CompletionDeadline_CancellationDuringResponseBuffering_PreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new BufferingCancellationHandler(cancellation.Cancel);
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>());

        var call = () => client.CompleteJsonAsync<Probe>("system", "user", cancellationToken: cancellation.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class BufferingCancellationHandler(Action cancel) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new CancellationContent(cancel)
            });
    }

    private sealed class CancellationContent(Action cancel) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult<Stream>(new NetworkResponseStream(new CancellingMemoryStream(cancel)));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new NotSupportedException();
        protected override bool TryComputeLength(out long length)
        {
            length = 2;
            return true;
        }
    }

    private sealed class CancellingMemoryStream(Action cancel) : MemoryStream(Encoding.UTF8.GetBytes("{}"))
    {
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            cancel();
            return base.CopyToAsync(destination, bufferSize, cancellationToken);
        }
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StreamContent(new NetworkResponseStream(new MemoryStream(Encoding.UTF8.GetBytes("{}"))))
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// The SDK buffers MemoryStream again during disposal. Network responses instead expose a stream
    /// whose cancellation can close it without triggering a second read during response cleanup.
    /// </summary>
    private sealed class NetworkResponseStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            => inner.CopyToAsync(destination, bufferSize, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static ResponsesClient BuildResponsesClient(HttpMessageHandler handler) =>
        new(
            credential: new ApiKeyCredential("test-key"),
            options: new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            });

    private sealed record Probe(string Value);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private static readonly string Body = ResponsesTestData.Text("{\"value\":\"ok\"}");

        public string? LastRequestBody { get; private set; }
        public string? LastRequestUri { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            return BuildResponse(request);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return BuildResponse(request);
        }

        private static HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(Body, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }
}
