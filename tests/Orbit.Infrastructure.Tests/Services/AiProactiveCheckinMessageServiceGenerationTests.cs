using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
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

    [Theory]
    [InlineData("en", "Pick a tiny step toward reading before the day ends at home.")]
    [InlineData("pt-BR", "Escolha um passo leve para retomar a leitura hoje com calma.")]
    public async Task GenerateMessageAsync_BodyAtSixtyTextElements_KeepsTrimmedBody(
        string language, string body)
    {
        new StringInfo(body).LengthInTextElements.Should().Be(60);
        var service = BuildService($"Title\n  {body}  ");

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, language);

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Title");
        result.Value.Body.Should().Be(body);
    }

    [Theory]
    [InlineData("en", "Pick a small step toward reading before the day ends at home.", "You have 2 habits still open today.")]
    [InlineData("pt-BR", "Escolha um passo curto para retomar a leitura hoje com calma.", "Você ainda tem 2 hábitos pendentes hoje.")]
    public async Task GenerateMessageAsync_BodyAtSixtyOneTextElements_ReplacesWholeBody(
        string language, string body, string expected)
    {
        new StringInfo(body).LengthInTextElements.Should().Be(61);
        var service = BuildService($"Title\n  {body}  ");

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, language);

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Title");
        result.Value.Body.Should().Be(expected);
    }

    [Theory]
    [InlineData("en", "You have 1 habit still open today.")]
    [InlineData("pt-BR", "Você ainda tem 1 hábito pendente hoje.")]
    public async Task GenerateMessageAsync_LongSingleLineBody_UsesSingularFallbackWithoutHabitTitle(
        string language, string expected)
    {
        var habitTitle = new string('a', 200);
        var service = BuildService($"Read {habitTitle} today.");

        var result = await service.GenerateMessageAsync("Alex", [habitTitle], 5, language);

        result.IsSuccess.Should().BeTrue();
        result.Value.Body.Should().Be(expected);
    }

    [Theory]
    [InlineData("en", "You have 2 habits still open today.")]
    [InlineData("pt-BR", "Você ainda tem 2 hábitos pendentes hoje.")]
    public async Task GenerateMessageAsync_TextElements_CountsEmojiAndCombiningMarksOnce(
        string language, string expectedFallback)
    {
        const string sentence = "Pick a tiny step toward reading before the day ends at home.";
        foreach (var body in new[]
                 {
                     "🙂 Pick a tiny step with reading before the day ends at home.",
                     "👩🏽‍💻 Pick a tiny step with reading before the day ends at home.",
                     "P\u0301" + sentence[1..]
                 })
        {
            new StringInfo(body).LengthInTextElements.Should().Be(60);
            body.Length.Should().BeGreaterThan(60);
            var service = BuildService($"Title\n{body}");
            var overflowService = BuildService($"Title\n{body}x");

            var kept = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, language);
            var replaced = await overflowService.GenerateMessageAsync("Alex", OffTrackHabits, 5, language);

            kept.IsSuccess.Should().BeTrue();
            kept.Value.Body.Should().Be(body);
            replaced.IsSuccess.Should().BeTrue();
            replaced.Value.Body.Should().Be(expectedFallback);
        }
    }

    [Fact]
    public async Task GenerateMessageAsync_TwoLines_ReturnsTitleAndBody()
    {
        var service = BuildService("Still time today, Alex\nMeditate is still open. Astra records it when you do it.");

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Still time today, Alex");
        result.Value.Body.Should().Be("Meditate is still open. Astra records it when you do it.");
    }

    [Theory]
    [InlineData("en", "take a moment to read with Astra.", "Take a moment to read with Astra.")]
    [InlineData("pt-BR", "água ajuda a retomar o dia com calma.", "Água ajuda a retomar o dia com calma.")]
    [InlineData("en", "\"take a moment to read with Astra.\"", "\"Take a moment to read with Astra.\"")]
    public async Task GenerateMessageAsync_SingleLowercaseLine_ReturnsSentenceCaseBody(
        string language, string modelBody, string expectedBody)
    {
        var service = BuildService(modelBody);

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, language);

        result.IsSuccess.Should().BeTrue();
        result.Value.Body.Should().Be(expectedBody);
    }

    [Fact]
    public async Task GenerateMessageAsync_NoActiveStreak_StillReturnsModelText()
    {
        var service = BuildService("Let's finish strong, Alex\nA couple of habits are still open today.");

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 0, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Let's finish strong, Alex");
        result.Value.Body.Should().Be("A couple of habits are still open today.");
    }

    [Fact]
    public async Task GenerateMessageAsync_SingleLineEnglish_UsesEnglishFallbackTitle()
    {
        var service = BuildService("A couple of habits are still open today.");

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Still time today, Alex");
        result.Value.Body.Should().Be("A couple of habits are still open today.");
    }

    [Fact]
    public async Task GenerateMessageAsync_SingleLinePortuguese_UsesPortugueseFallbackTitle()
    {
        var service = BuildService("Alguns habitos ainda estao abertos hoje.");

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ainda dá tempo hoje, Alex");
        result.Value.Body.Should().Be("Alguns habitos ainda estao abertos hoje.");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponseEnglish_ReturnsEnglishFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Still time today, Alex");
        result.Value.Body.Should().Be("You have 2 habits still open today.");
    }

    [Fact]
    public async Task GenerateMessageAsync_BlankResponsePortuguese_ReturnsPortugueseFallback()
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 0, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ainda dá tempo hoje, Alex");
        result.Value.Body.Should().Be("Você ainda tem 2 hábitos pendentes hoje.");
    }

    [Fact]
    public async Task GenerateMessageAsync_AiCallFailsEnglish_ReturnsEnglishFallback()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Still time today, Alex");
        result.Value.Body.Should().Be("You have 2 habits still open today.");
    }

    [Fact]
    public async Task GenerateMessageAsync_AiCallFailsPortuguese_ReturnsPortugueseFallback()
    {
        var service = BuildService("boom", HttpStatusCode.BadRequest);

        var result = await service.GenerateMessageAsync("Alex", OffTrackHabits, 0, "pt-BR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Title.Should().Be("Ainda dá tempo hoje, Alex");
        result.Value.Body.Should().Be("Você ainda tem 2 hábitos pendentes hoje.");
    }

    /// <summary>
    /// <c>ProactiveCheckinSchedulerService</c> sends as soon as one habit is off track, so a single
    /// open habit is the ordinary case and the copy must not call it a few. The old fallback also
    /// promised that Astra recorded the rest, which <c>LogHabitTool</c> never does: it records only
    /// the habit a person names.
    /// </summary>
    [Theory]
    [InlineData("en", "You have 1 habit still open today.")]
    [InlineData("pt-BR", "Você ainda tem 1 hábito pendente hoje.")]
    public async Task GenerateMessageAsync_OneOpenHabit_CountsItAsOne(string language, string expected)
    {
        var service = BuildService("   ");

        var result = await service.GenerateMessageAsync("Alex", ["Meditate"], 5, language);

        result.IsSuccess.Should().BeTrue();
        result.Value.Body.Should().Be(expected);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task GenerateMessageAsync_TheFallbackNeverPromisesAutomaticLogging(string language)
    {
        var service = BuildService("   ");

        var oneOpen = await service.GenerateMessageAsync("Alex", ["Meditate"], 5, language);
        var manyOpen = await service.GenerateMessageAsync("Alex", OffTrackHabits, 5, language);

        foreach (var body in new[] { oneOpen.Value.Body, manyOpen.Value.Body })
        {
            body.Should().NotContainEquivalentOf("records the rest");
            body.Should().NotContainEquivalentOf("cuida do resto");
        }
    }

    internal static AiProactiveCheckinMessageService BuildService(string content, HttpStatusCode status = HttpStatusCode.OK)
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
