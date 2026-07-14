using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI;
using OpenAI.Chat;
using Orbit.Application.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AiGoalReviewServiceGenerationTests
{
    [Fact]
    public async Task GenerateReviewAsync_EmptyContext_ReturnsNoGoalsData()
    {
        var service = BuildService("ignored");

        var result = await service.GenerateReviewAsync("   ", "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.NoGoalsData.Message);
    }

    [Fact]
    public async Task GenerateReviewAsync_ModelReturnsText_ReturnsTrimmedReview()
    {
        var service = BuildService("```\nYou are on track with running. Keep the momentum.\n```");

        var result = await service.GenerateReviewAsync("Goal: run 100km, 60km done", "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("You are on track with running. Keep the momentum.");
    }

    [Fact]
    public async Task GenerateReviewAsync_ModelReturnsBlank_ReturnsEmptyResponseFailure()
    {
        var service = BuildService("   ");

        var result = await service.GenerateReviewAsync("Goal: read more", "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiEmptyResponse.Message);
    }

    [Fact]
    public async Task GenerateReviewAsync_AiCallFails_ReturnsUnavailable()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateReviewAsync("Goal: meditate", "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiGoalReviewUnavailable.Message);
    }

    private static AiGoalReviewService BuildService(string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        var chatClient = new ChatClient(
            model: "gpt-test",
            credential: new ApiKeyCredential("test-key"),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(
                    new HttpClient(new CannedChatHandler(content, status))),
            });
        var aiClient = new AiCompletionClient(
            chatClient, NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        return new AiGoalReviewService(aiClient, NullLogger<AiGoalReviewService>.Instance);
    }

    private sealed class CannedChatHandler(string content, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"error\":{\"message\":\"bad\"}}", Encoding.UTF8, "application/json"),
                });

            var escaped = JsonSerializer.Serialize(content);
            var body =
                "{\"id\":\"chatcmpl-test\",\"object\":\"chat.completion\",\"created\":1700000000,\"model\":\"gpt-test\","
                + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" + escaped + "},\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
