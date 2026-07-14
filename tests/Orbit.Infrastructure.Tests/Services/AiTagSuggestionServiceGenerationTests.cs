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

public class AiTagSuggestionServiceGenerationTests
{
    private static readonly string[] NoExistingTags = [];

    [Fact]
    public async Task SuggestTagsAsync_ValidTags_ReturnsTrimmedTagsWithBlanksFiltered()
    {
        var service = BuildService(TagsJson(" health ", "fitness", "", "  "));

        var result = await service.SuggestTagsAsync("Morning run", "Jog the park", NoExistingTags, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Equal("health", "fitness");
    }

    [Fact]
    public async Task SuggestTagsAsync_EmptyTagsArray_ReturnsEmptyResponseFailure()
    {
        var service = BuildService(TagsJson());

        var result = await service.SuggestTagsAsync("Morning run", null, NoExistingTags, "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiEmptyResponse.Message);
    }

    [Fact]
    public async Task SuggestTagsAsync_WhitespaceOnlyTags_ReturnsEmptyResponseFailure()
    {
        var service = BuildService(TagsJson("  ", ""));

        var result = await service.SuggestTagsAsync("Morning run", null, NoExistingTags, "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiEmptyResponse.Message);
    }

    [Fact]
    public async Task SuggestTagsAsync_MissingTagsProperty_ReturnsEmptyResponseFailure()
    {
        var service = BuildService("{}");

        var result = await service.SuggestTagsAsync("Morning run", null, NoExistingTags, "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiEmptyResponse.Message);
    }

    [Fact]
    public async Task SuggestTagsAsync_EmptyCompletion_ReturnsEmptyResponseFailure()
    {
        var service = BuildService("   ");

        var result = await service.SuggestTagsAsync("Morning run", null, NoExistingTags, "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiEmptyResponse.Message);
    }

    [Fact]
    public async Task SuggestTagsAsync_AiCallFails_ReturnsTagSuggestionUnavailable()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.SuggestTagsAsync("Morning run", null, NoExistingTags, "en");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(ErrorMessages.AiTagSuggestionUnavailable.Message);
    }

    private static string TagsJson(params string[] tags) => JsonSerializer.Serialize(new { tags });

    private static AiTagSuggestionService BuildService(string content, HttpStatusCode status = HttpStatusCode.OK)
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
        return new AiTagSuggestionService(aiClient, NullLogger<AiTagSuggestionService>.Instance);
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
