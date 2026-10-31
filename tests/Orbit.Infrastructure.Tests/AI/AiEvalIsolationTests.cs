#pragma warning disable OPENAI001

using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.AI;

public class AiEvalIsolationTests
{
    [Theory]
    [InlineData("chat", true, true)]
    [InlineData("stream", true, true)]
    [InlineData("text", true, true)]
    [InlineData("json", true, true)]
    [InlineData("chat", false, true)]
    [InlineData("stream", false, true)]
    [InlineData("text", false, true)]
    [InlineData("json", false, true)]
    [InlineData("chat", true, false)]
    [InlineData("stream", true, false)]
    [InlineData("text", true, false)]
    [InlineData("json", true, false)]
    public async Task Calls_SelectCredentialByAccountForEveryCompletionPath(string kind, bool eval, bool configured)
    {
        var userId = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StagingEval:AccountIds"] = $"invalid, {Guid.NewGuid()}, {(eval ? userId : Guid.NewGuid())}"
        }).Build();
        var usage = Substitute.For<IAiUsageRecorder>();
        var handler = new CredentialCapture(kind == "stream");
        var client = new AiCompletionClient(Options.Create(new AiSettings
        {
            ApiKey = "production-test-key",
            EvalApiKey = configured ? "eval-test-key" : "",
            BaseUrl = "https://orbit.test/v1"
        }), NullLogger<AiCompletionClient>.Instance, usage,
            new HttpClientPipelineTransport(new HttpClient(handler)), configuration: config);
        var budgeted = AiBudgetTestDouble.Wrap(client);
        if (kind is "chat" or "stream")
        {
            var service = new AiIntentService(budgeted, usage, NullLogger<AiIntentService>.Instance);
            var result = await service.SendWithToolsAsync(new("hello", "system", [], userId),
                kind == "stream" ? _ => Task.CompletedTask : null);
            result.IsSuccess.Should().BeTrue(result.Error);
        }
        else
        {
            using var context = AiCallContext.Begin(userId);
            if (kind == "text") await budgeted.CompleteTextAsync("system", "hello");
            else await budgeted.CompleteJsonAsync<object>("system", "hello");
        }

        handler.Credentials.Should().Equal(eval && configured ? "eval-test-key" : "production-test-key");
        await usage.Received(1).RecordAsync(Arg.Any<string>(), "gpt-6-luna", 40, 100, 30, 130,
            Arg.Any<CancellationToken>(), userId, Arg.Any<AiCallTelemetry>(), 10, 20);
    }

    private sealed class CredentialCapture(bool streaming) : HttpMessageHandler
    {
        public List<string?> Credentials { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Credentials.Add(request.Headers.Authorization?.Parameter);
            var response = ResponsesTestData.Response("{}", 100, 30, 40);
            response.Usage.InputTokenDetails.CacheWriteTokenCount = 10;
            response.Usage.OutputTokenDetails = new() { ReasoningTokenCount = 20 };
            var body = streaming ? ResponsesTestData.Stream(response) : ModelReaderWriter.Write(response).ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, streaming ? "text/event-stream" : "application/json")
            });
        }
    }
}
