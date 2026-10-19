using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI;
using OpenAI.Chat;
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
        var client = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);

        await client.CompleteJsonAsync<Probe>("You are a helpful assistant. Respond only with valid JSON.", "extract facts", purpose: "fact_extraction", tier: AiModelTier.SubTask);

        handler.LastRequestBody.Should().NotBeNull();
        handler.LastRequestBody.Should().NotContain("temperature");
        await usageRecorder.Received(1).RecordAsync(
            "fact_extraction", "subtask-test", 0, 1, 2, 3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteJsonAsync_PrimaryTier_SendsTemperature()
    {
        var handler = new CapturingHandler();
        var client = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());

        await client.CompleteJsonAsync<Probe>("You are a helpful assistant. Respond only with valid JSON.", "extract facts", temperature: 0.1, purpose: "json", tier: AiModelTier.Primary);

        handler.LastRequestBody.Should().Contain("temperature");
    }

    [Fact]
    public async Task CompleteJsonAsync_WithUserId_AttributesRecordedUsage()
    {
        var handler = new CapturingHandler();
        var usageRecorder = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance, usageRecorder);
        var userId = Guid.NewGuid();

        await client.CompleteJsonAsync<Probe>("system", "user", userId: userId);

        await usageRecorder.Received(1).RecordAsync(
            "json", "primary-test", 0, 1, 2, 3, Arg.Any<CancellationToken>(), userId);
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
    [InlineData(AiModelTier.Primary, "gpt-4.1-mini", "gpt-5.4-nano", true)]
    [InlineData(AiModelTier.SubTask, "gpt-4.1-mini", "gpt-5.4-nano", false)]
    [InlineData(AiModelTier.SubTask, "gpt-4.1-mini", "gpt-4.1-mini", true)]
    public void ShouldApplyTemperature_SuppressedOnlyForDistinctSubTaskModel(
        AiModelTier tier, string primary, string subTask, bool expected)
        => AiCompletionClient.ShouldApplyTemperature(tier, primary, subTask).Should().Be(expected);

    [Theory]
    [InlineData("json")]
    [InlineData("text")]
    [InlineData("chat")]
    [InlineData("stream")]
    public async Task CompletionDeadline_CancelsTheWholeCallIncludingRetryWaits(string kind)
    {
        var client = new AiCompletionClient(BuildChatClient(new WaitingHandler()), NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>(), TimeSpan.FromMilliseconds(50));
        var intent = new AiIntentService(Orbit.Infrastructure.Tests.AiBudgetTestDouble.Wrap(client), Substitute.For<IAiUsageRecorder>(), NullLogger<AiIntentService>.Instance);
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

    [Theory]
    [InlineData("text")]
    [InlineData("json")]
    [InlineData("chat")]
    public async Task BudgetReservation_CoversSerializedRequestAndBoundedOutput(string kind)
    {
        var handler = new CapturingHandler();
        var raw = new AiCompletionClient(BuildChatClient(handler), NullLogger<AiCompletionClient>.Instance,
            Substitute.For<IAiUsageRecorder>());
        var budget = Substitute.For<IAiDailyBudget>();
        long reserved = 0;
        budget.ReserveAsync(Arg.Any<Guid>(), Arg.Do<long>(value => reserved = value), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        var userId = Guid.NewGuid();
        var http = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        http.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId.ToString())], "Test"));
        var client = new BudgetedAiCompletionClient(raw, budget, http, NullLogger<BudgetedAiCompletionClient>.Instance);
        const string prompt = "Plan a caminhada 🏃";
        if (kind == "text")
            await client.CompleteTextAsync("system", prompt);
        else if (kind == "json")
            await client.CompleteJsonAsync<Probe>("system", prompt);
        else
        {
            var intent = new AiIntentService(client, Substitute.For<IAiUsageRecorder>(), NullLogger<AiIntentService>.Instance);
            await intent.SendWithToolsAsync(new AiToolRequest(prompt, "system", [], userId));
        }

        using var request = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var outputLimit = request.RootElement.GetProperty("max_completion_tokens").GetInt32();
        outputLimit.Should().Be(AiTokenReservation.DefaultMaxOutputTokens);
        reserved.Should().BeGreaterThanOrEqualTo(Encoding.UTF8.GetByteCount(handler.LastRequestBody!) + outputLimit);
        reserved.Should().BeLessThan(Orbit.Application.Common.AppConfigKeys.DefaultFreeAiTokensPerDay);
    }

    [Fact]
    public void BudgetReservation_MediaRetainsWorstCaseInsteadOfTreatingCompressedBytesAsTokens()
    {
        var message = new UserChatMessage(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(new byte[16]), "image/png"));

        var tokens = AiTokenReservation.Estimate([message], new ChatCompletionOptions());

        tokens.Should().BeGreaterThan(Orbit.Application.Common.AppConstants.MaxAiRequestTotalTokens);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        }
    }

    private static ChatClient BuildChatClient(HttpMessageHandler handler) =>
        new(
            model: "gpt-test",
            credential: new ApiKeyCredential("test-key"),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
            });

    private sealed record Probe(string Value);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private const string Body = """
            {"id":"chatcmpl-test","object":"chat.completion","created":1700000000,"model":"gpt-test",
             "choices":[{"index":0,"message":{"role":"assistant","content":"{\"value\":\"ok\"}"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3}}
            """;

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

        private static HttpResponseMessage BuildResponse(HttpRequestMessage request)
        {
            var content = new StringContent(Body, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
        }
    }
}
