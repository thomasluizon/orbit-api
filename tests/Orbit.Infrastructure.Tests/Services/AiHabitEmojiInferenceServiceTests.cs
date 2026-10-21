#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AiHabitEmojiInferenceServiceTests
{
    [Fact]
    public async Task InferAsync_UsesRealJsonResponseAndAttributesUsageToUser()
    {
        var userId = Guid.NewGuid();
        var habitId = Guid.NewGuid();
        var body = ResponsesTestData.Text(System.Text.Json.JsonSerializer.Serialize(new
        {
            emojis = new Dictionary<string, string> { [habitId.ToString()] = "💊" }
        }));
        var handler = new JsonHandler(body);
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildResponsesClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var service = new AiHabitEmojiInferenceService(Orbit.Infrastructure.Tests.AiBudgetTestDouble.Wrap(client), NullLogger<AiHabitEmojiInferenceService>.Instance);

        var result = await service.InferAsync(
            userId,
            [new HabitEmojiInferenceInput(habitId, "Rosuvastatina 10 mg", null)]);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Contain(habitId, "💊");
        handler.LastRequestBody.Should().Contain("Rosuvastatina 10 mg");
        handler.LastRequestBody.Should().Contain("json_object");
        handler.LastRequestBody.Should().NotContain("temperature");
        await usageRecorder.Received(1).RecordAsync(
            "habit_emoji_inference", "gpt-6-luna", 0, 1, 2, 3, Arg.Any<CancellationToken>(), userId,
            Arg.Is<AiCallTelemetry>(call => call.HasUsage && !call.IsError && call.ToolNames.Count == 0));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("invalid_id")]
    [InlineData("incomplete")]
    public async Task InferAsync_InvalidProviderMapping_IsNotAccepted(string fault)
    {
        var habitId = Guid.NewGuid();
        var mappings = new Dictionary<string, string> { [habitId.ToString()] = "📚" };
        if (fault == "missing") mappings.Clear();
        if (fault == "unknown") mappings[Guid.NewGuid().ToString()] = "📚";
        if (fault == "invalid_id") mappings["unrequested"] = "📚";
        var response = ResponsesTestData.Response(JsonSerializer.Serialize(new { emojis = mappings }));
        if (fault == "incomplete") response.Status = ResponseStatus.Incomplete;
        var client = new AiCompletionClient(BuildResponsesClient(new JsonHandler(ModelReaderWriter.Write(response).ToString())),
            NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        var service = new AiHabitEmojiInferenceService(AiBudgetTestDouble.Wrap(client), NullLogger<AiHabitEmojiInferenceService>.Instance);

        var result = await service.InferAsync(Guid.NewGuid(), [new(habitId, "Read", null)]);

        result.IsFailure.Should().BeTrue();
    }

    private static ResponsesClient BuildResponsesClient(HttpMessageHandler handler) =>
        new(
            credential: new ApiKeyCredential("test-key"),
            options: new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler))
            });

    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
