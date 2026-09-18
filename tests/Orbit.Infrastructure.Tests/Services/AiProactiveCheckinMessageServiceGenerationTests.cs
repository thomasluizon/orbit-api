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

public class AiProactiveCheckinMessageServiceGenerationTests
{
    private static readonly string[] OffTrackHabits = ["Meditate", "Read"];

    [Fact]
    public async Task GenerateMessageAsync_TwoLines_ReturnsTitleAndBody()
    {
        var service = BuildService("Still time today, Thomas\nMeditate is still open. Astra records it when you do it.");

        var result = await service.GenerateMessageAsync("Thomas", OffTrackHabits, 5, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Still time today, Thomas");
        result.Value.Body.Should().Be("Meditate is still open. Astra records it when you do it.");
    }

    [Fact]
    public async Task GenerateMessageAsync_NoActiveStreak_StillReturnsModelText()
    {
        var service = BuildService("Let's finish strong, Thomas\nA couple of habits are still open today.");

        var result = await service.GenerateMessageAsync("Thomas", OffTrackHabits, 0, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Let's finish strong, Thomas");
        result.Value.Body.Should().Be("A couple of habits are still open today.");
    }

    [Fact]
    public async Task GenerateMessageAsync_SingleLineEnglish_UsesEnglishFallbackTitle()
    {
        var service = BuildService("A couple of habits are still open today.");

        var result = await service.GenerateMessageAsync("Thomas", OffTrackHabits, 5, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Still time today, Thomas");
        result.Value.Body.Should().Be("A couple of habits are still open today.");
    }

    [Fact]
    public async Task GenerateMessageAsync_SingleLinePortuguese_UsesPortugueseFallbackTitle()
    {
        var service = BuildService("Alguns habitos ainda estao abertos hoje.");

        var result = await service.GenerateMessageAsync("Thomas", OffTrackHabits, 5, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ainda dá tempo hoje, Thomas");
        result.Value.Body.Should().Be("Alguns habitos ainda estao abertos hoje.");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponseEnglish_ReturnsEnglishFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Thomas", OffTrackHabits, 5, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Still time today, Thomas");
        result.Value.Body.Should().Be("Some habits are still open today. Pick the easiest one and tell Astra when you do it.");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponsePortuguese_ReturnsPortugueseFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Thomas", OffTrackHabits, 0, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ainda dá tempo hoje, Thomas");
        result.Value.Body.Should().Be("Ainda há hábitos abertos hoje. Escolha o mais fácil e conte à Astra quando fizer.");
    }

    [Fact]
    public async Task GenerateMessageAsync_AiCallFailsEnglish_ReturnsEnglishFallback()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateMessageAsync("Thomas", OffTrackHabits, 5, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Still time today, Thomas");
        result.Value.Body.Should().Be("Some habits are still open today. Pick the easiest one and tell Astra when you do it.");
    }

    [Fact]
    public async Task GenerateMessageAsync_AiCallFailsPortuguese_ReturnsPortugueseFallback()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateMessageAsync("Thomas", OffTrackHabits, 0, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ainda dá tempo hoje, Thomas");
        result.Value.Body.Should().Be("Ainda há hábitos abertos hoje. Escolha o mais fácil e conte à Astra quando fizer.");
    }

    /// <summary>
    /// <c>ProactiveCheckinSchedulerService</c> sends as soon as one habit is off track, so a single
    /// open habit is the ordinary case and the copy must not call it a few. The old fallback also
    /// promised that Astra recorded the rest, which <c>LogHabitTool</c> never does: it records only
    /// the habit a person names.
    /// </summary>
    [Theory]
    [InlineData("en", "One habit is still open today. Tell Astra when you do it.")]
    [InlineData("pt-BR", "Um hábito segue aberto hoje. Conte à Astra quando você fizer.")]
    public async Task GenerateMessageAsync_OneOpenHabit_CountsItAsOne(string language, string expected)
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Thomas", ["Meditate"], 5, language);

        result.IsSuccess.Should().BeTrue();
        result.Value.Body.Should().Be(expected);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task GenerateMessageAsync_TheFallbackNeverPromisesAutomaticLogging(string language)
    {
        var service = BuildService("   ");

        var oneOpen = await service.GenerateMessageAsync("Thomas", ["Meditate"], 5, language);
        var manyOpen = await service.GenerateMessageAsync("Thomas", OffTrackHabits, 5, language);

        foreach (var body in new[] { oneOpen.Value.Body, manyOpen.Value.Body })
        {
            body.Should().NotContainEquivalentOf("records the rest");
            body.Should().NotContainEquivalentOf("cuida do resto");
        }
    }

    private static AiProactiveCheckinMessageService BuildService(string content, HttpStatusCode status = HttpStatusCode.OK)
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
        return new AiProactiveCheckinMessageService(aiClient, NullLogger<AiProactiveCheckinMessageService>.Instance);
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
