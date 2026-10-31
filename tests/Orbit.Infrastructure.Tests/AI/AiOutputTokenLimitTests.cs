#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;
using Orbit.Domain.Models;
using Microsoft.ML.Tokenizers;
using System.Text.Encodings.Web;

namespace Orbit.Infrastructure.Tests.AI;

public class AiOutputTokenLimitTests
{
    [Theory]
    [InlineData("proactive_item", 512)]
    [InlineData("slip_alert", 512)]
    [InlineData("tag_suggestion", 1024)]
    [InlineData("habit_emoji_inference", 1024)]
    [InlineData("offer_selection", 1024)]
    [InlineData("habit_interpret_date", 128)]
    [InlineData("habit_drafts", 4096)]
    [InlineData("habit_suggest", 2048)]
    [InlineData("reschedule_suggestion", 2048)]
    [InlineData("retrospective", 2048)]
    [InlineData("goal_review", 2048)]
    [InlineData("daily_summary", 2048)]
    [InlineData("text", 2048)]
    [InlineData("json", 2048)]
    public async Task Completions_EnforcePurposeCapForDirectAndBudgetedCalls(string purpose, int expected)
    {
        var handler = new LimitCapture();
        var client = new AiCompletionClient(new ResponsesClient(new System.ClientModel.ApiKeyCredential("test-key"),
            new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler))
            }), NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        var budgeted = AiBudgetTestDouble.Wrap(client);
        await client.CompleteTextAsync("system", "hello", purpose: purpose);
        await client.CompleteJsonAsync<object>("system", "hello", maxOutputTokens: 8192, purpose: purpose);
        await budgeted.CompleteTextAsync("system", "hello", purpose: purpose);
        await budgeted.CompleteJsonAsync<object>("system", "hello", maxOutputTokens: 8192, purpose: purpose);

        handler.Limits.Should().Equal(expected, expected, expected, expected);
    }

    [Fact]
    public async Task OfferSelection_KeepsCapAndUsagePurposeAcrossContinuation()
    {
        var handler = new LimitCapture();
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(new ResponsesClient(new System.ClientModel.ApiKeyCredential("test-key"),
            new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler))
            }), NullLogger<AiCompletionClient>.Instance, usage);
        var service = new AiIntentService(AiBudgetTestDouble.Wrap(client), usage, NullLogger<AiIntentService>.Instance);
        var first = await service.SendWithToolsAsync(new("Select", "system", [], Guid.NewGuid()) { Purpose = "offer_selection" });
        first.IsSuccess.Should().BeTrue(first.Error);
        var second = await service.ContinueWithToolResultsAsync(first.Value.ConversationContext!, []);

        second.IsSuccess.Should().BeTrue(second.Error);
        handler.Limits.Should().Equal(1024, 1024);
        usage.ReceivedCalls().Should().HaveCount(2).And.OnlyContain(call => (string)call.GetArguments()[0]! == "offer_selection");
    }

    [Fact]
    public async Task ChatCap_ReplaysLargestCapturedBulkCreateWithReasoningHeadroom()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..",
            "tests/Orbit.Application.Tests/Fixtures/GateNineMedical/g9i-composition-medical-batch-pt-BR-r3.json"));
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var call = fixture.RootElement.GetProperty("toolCalls")[0];
        var args = call.GetProperty("arguments");
        var payload = JsonSerializer.Serialize(args, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var tokens = TiktokenTokenizer.CreateForEncoding("o200k_base").CountTokens(payload);
        var response = ResponsesTestData.Response();
        response.OutputItems.Add(ResponseItem.CreateFunctionCallItem("captured", "bulk_create_habits", BinaryData.FromString(payload)));
        var handler = new LimitCapture(response);
        var usage = Substitute.For<IAiUsageRecorder>();
        var client = new AiCompletionClient(new ResponsesClient(new System.ClientModel.ApiKeyCredential("test-key"),
            new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler))
            }), NullLogger<AiCompletionClient>.Instance, usage);
        var service = new AiIntentService(AiBudgetTestDouble.Wrap(client), usage, NullLogger<AiIntentService>.Instance);
        var tool = StrictChatToolSchemaTests.AllTools().Single(tool => tool.Name == "bulk_create_habits");
        var result = await service.SendWithToolsAsync(new("Create the captured batch", "system",
            [new { name = tool.Name, description = tool.Description, parameters = tool.GetParameterSchema() }], Guid.NewGuid()));

        result.IsSuccess.Should().BeTrue(result.Error);
        JsonElement.DeepEquals(result.Value.ToolCalls.Should().ContainSingle().Which.Args, args).Should().BeTrue();
        tokens.Should().Be(1189);
        handler.Limits.Should().ContainSingle().Which.Should().Be(4096);
        (handler.Limits[0] - tokens).Should().BeGreaterThanOrEqualTo(2048);
    }

    private sealed class LimitCapture(ResponseResult? response = null) : HttpMessageHandler
    {
        public List<int?> Limits { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Limits.Add(body.RootElement.TryGetProperty("max_output_tokens", out var limit) ? limit.GetInt32() : null);
            return new(HttpStatusCode.OK) { Content = new StringContent(ModelReaderWriter.Write(response ?? ResponsesTestData.Response("{}")).ToString(), Encoding.UTF8, "application/json") };
        }
    }
}
