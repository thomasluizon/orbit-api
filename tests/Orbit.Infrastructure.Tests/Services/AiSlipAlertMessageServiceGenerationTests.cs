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
        var service = BuildService("Your usual time for Smoking\nIt tends to show up around now. You can let it pass.");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Your usual time for Smoking");
        result.Value.Body.Should().Be("It tends to show up around now. You can let it pass.");
    }

    [Fact]
    public async Task GenerateMessageAsync_NullPeakHour_StillReturnsModelText()
    {
        var service = BuildService("A clean Monday\nOne clean day at a time.");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Monday, null, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("A clean Monday");
        result.Value.Body.Should().Be("One clean day at a time.");
    }

    [Fact]
    public async Task GenerateMessageAsync_SingleLineEnglish_UsesEnglishFallbackTitle()
    {
        var service = BuildService("Keep your streak clean today.");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ahead of the usual time for Smoking");
        result.Value.Body.Should().Be("Keep your streak clean today.");
    }

    [Fact]
    public async Task GenerateMessageAsync_SingleLinePortuguese_UsesPortugueseFallbackTitle()
    {
        var service = BuildService("Mantenha a sequencia limpa hoje.");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Antes do horário de costume: Smoking");
        result.Value.Body.Should().Be("Mantenha a sequencia limpa hoje.");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponseEnglish_ReturnsEnglishFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ahead of the usual time for Smoking");
        result.Value.Body.Should().Be("This tends to come up later today. You can let it pass.");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponsePortuguese_ReturnsPortugueseFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, null, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Um lembrete tranquilo: Smoking");
        result.Value.Body.Should().Be("Hoje é um dos dias em que isso costuma aparecer. Você pode deixar passar.");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponseEnglishWithoutPeak_ReturnsDayFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, null, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("A quiet note about Smoking");
        result.Value.Body.Should().Be("Today is one of the days this tends to come up. You can let it pass.");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponsePortugueseWithPeak_ReturnsEarlyFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Antes do horário de costume: Smoking");
        result.Value.Body.Should().Be("Isso costuma aparecer mais tarde hoje. Você pode deixar passar.");
    }

    [Fact]
    public async Task GenerateMessageAsync_AiCallFailsEnglish_ReturnsEnglishFallback()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, 14, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ahead of the usual time for Smoking");
        result.Value.Body.Should().Be("This tends to come up later today. You can let it pass.");
    }

    [Fact]
    public async Task GenerateMessageAsync_AiCallFailsPortuguese_ReturnsPortugueseFallback()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, null, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Um lembrete tranquilo: Smoking");
        result.Value.Body.Should().Be("Hoje é um dos dias em que isso costuma aparecer. Você pode deixar passar.");
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
