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
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Services;

public class AiSlipAlertMessageServiceGenerationTests
{
    [Fact]
    public async Task GenerateMessageAsync_TwoLines_ReturnsTitleAndBody()
    {
        var service = BuildService("Stay strong today!\nYou tend to reach for it now -- ride it out.");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Stay strong today!");
        result.Value.Body.Should().Be("You tend to reach for it now -- ride it out.");
    }

    [Fact]
    public async Task GenerateMessageAsync_NullPeakHour_StillReturnsModelText()
    {
        var service = BuildService("You've got this!\nOne clean day at a time.");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Monday, null, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("You've got this!");
        result.Value.Body.Should().Be("One clean day at a time.");
    }

    [Fact]
    public async Task GenerateMessageAsync_SingleLineEnglish_UsesEnglishFallbackTitle()
    {
        var service = BuildService("Keep your streak clean today.");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Heads up: Smoking");
        result.Value.Body.Should().Be("Keep your streak clean today.");
    }

    [Fact]
    public async Task GenerateMessageAsync_SingleLinePortuguese_UsesPortugueseFallbackTitle()
    {
        var service = BuildService("Mantenha a sequencia limpa hoje.");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Fique atento: Smoking");
        result.Value.Body.Should().Be("Mantenha a sequencia limpa hoje.");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponseEnglish_ReturnsEnglishFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Heads up: Smoking");
        result.Value.Body.Should().Be("You tend to slip around this time. Stay strong -- you've got this!");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponsePortuguese_ReturnsPortugueseFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, null, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Fique atento: Smoking");
        result.Value.Body.Should().Be("Você costuma deslizar por volta desse horário. Força -- você consegue!");
    }

    [Fact]
    public async Task GenerateMessageAsync_AiCallFailsEnglish_ReturnsEnglishFallback()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Heads up: Smoking");
        result.Value.Body.Should().Be("You tend to slip around this time. Stay strong -- you've got this!");
    }

    [Fact]
    public async Task GenerateMessageAsync_AiCallFailsPortuguese_ReturnsPortugueseFallback()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, null, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Fique atento: Smoking");
        result.Value.Body.Should().Be("Você costuma deslizar por volta desse horário. Força -- você consegue!");
    }

    private static AiSlipAlertMessageService BuildService(string content, HttpStatusCode status = HttpStatusCode.OK)
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
        return new AiSlipAlertMessageService(aiClient, NullLogger<AiSlipAlertMessageService>.Instance);
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
