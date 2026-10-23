#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Common;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

public class AiSlipAlertMessageServiceGenerationTests
{
    [Theory]
    [InlineData("Cheat diet", "pt-BR", "Hoje é dia de seguir a dieta!", "Você pode manter sua alimentação como planejou nesta sexta-feira.")]
    [InlineData("Comer besteira", "en", "Stick with your meals today", "You can follow your meal plan this Friday.")]
    [InlineData("Smoking", "pt-BR", "Hoje sem cigarro", "Você pode passar esta sexta-feira sem fumar.")]
    public async Task GenerateMessageAsync_ForeignHabitTitle_ReturnsBothModelLinesInAccountLanguage(
        string habitTitle, string language, string title, string body)
    {
        using var fixture = new CompletionFixture($"{title}\n{body}");

        var result = await fixture.Service.GenerateMessageAsync(habitTitle, DayOfWeek.Friday, 14, language);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be((title, body));
        fixture.Handler.RequestStrings.Should().Contain(AiSlipAlertMessageService.SystemPrompt);
        fixture.Handler.RequestStrings.Should().Contain(
            AiSlipAlertMessageService.BuildPrompt(habitTitle, DayOfWeek.Friday, 14, language));
        AssertNoHabitTitle(result, habitTitle);
    }

    [Theory]
    [InlineData("Cheat diet", "pt-BR", "Hoje, siga sua escolha\nÉ sexta-feira. Se surgir a vontade ligada a Cheat diet, deixe passar.")]
    [InlineData("Cheat diet", "pt-BR", "Um lembrete sobre Cheat diet\nVocê pode seguir sua escolha hoje.")]
    [InlineData("Smoking", "pt-BR", "Hoje sem Smoking\nVocê pode seguir sua escolha hoje.")]
    [InlineData("Comer besteira", "en", "Keep your choice today\nLet the urge tied to Comer besteira pass.")]
    [InlineData("Fumar", "en", "A note about Fumar\nKeep your choice today.")]
    [InlineData("Cheat diet", "pt-BR", "**Hoje sem cheat diet**\nVocê pode seguir sua escolha hoje.")]
    public async Task GenerateMessageAsync_RawForeignTitleInEitherLine_ReturnsNeutralFallback(
        string habitTitle, string language, string response)
    {
        using var fixture = new CompletionFixture(response);

        var result = await fixture.Service.GenerateMessageAsync(habitTitle, DayOfWeek.Friday, 14, language);

        AssertFallback(result, habitTitle, language);
    }

    [Theory]
    [InlineData("Smoking", "en")]
    [InlineData("Fumar", "pt-BR")]
    [InlineData("Pizza", "en")]
    [InlineData("Pizza", "pt-BR")]
    [InlineData("Café", "en")]
    [InlineData("Fast food", "pt-BR")]
    [InlineData("Cheat dieta", "pt-BR")]
    [InlineData("Cheetos", "pt-BR")]
    public async Task GenerateMessageAsync_LocalOrUncertainTitle_PrefersModelText(string habitTitle, string language)
    {
        var title = $"A note about {habitTitle}";
        var body = $"Your choice about {habitTitle} today.";
        using var fixture = new CompletionFixture($"{title}\n{body}");

        var result = await fixture.Service.GenerateMessageAsync(habitTitle, DayOfWeek.Friday, 14, language);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be((title, body));
    }

    [Theory]
    [InlineData("**Stay smoke-free today**\nYou can *keep your choice* today.")]
    [InlineData("\r\nStay smoke-free today\r\n  \r\nYou can keep your choice today.\r\n")]
    public async Task GenerateMessageAsync_TwoLines_NormalizesMarkdownAndWhitespace(string response)
    {
        using var fixture = new CompletionFixture(response);

        var result = await fixture.Service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, null, "en");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(("Stay smoke-free today", "You can keep your choice today."));
    }

    [Theory]
    [InlineData("en", 14, "")]
    [InlineData("pt-BR", null, "")]
    [InlineData("en", null, "   ")]
    [InlineData("pt-BR", 14, "   ")]
    [InlineData("en", 14, "```\n```")]
    [InlineData("pt-BR", null, "```\n```")]
    [InlineData("en", 14, "Keep your choice today.")]
    [InlineData("pt-BR", null, "Você pode seguir sua escolha hoje.")]
    [InlineData("en", null, "Title\nBody\nExtra line")]
    [InlineData("pt-BR", 14, "Título\nCorpo\nOutra linha")]
    public async Task GenerateMessageAsync_EmptyOrMalformedResponse_ReturnsNeutralFallback(
        string language, int? peakHour, string response)
    {
        const string habitTitle = "Cheat diet";
        using var fixture = new CompletionFixture(response);

        var result = await fixture.Service.GenerateMessageAsync(habitTitle, DayOfWeek.Friday, peakHour, language);

        AssertFallback(result, habitTitle, language);
    }

    [Theory]
    [InlineData("en", 14)]
    [InlineData("en", null)]
    [InlineData("pt-BR", 14)]
    [InlineData("pt-BR", null)]
    public async Task GenerateMessageAsync_ProviderFails_ReturnsNeutralFallback(string language, int? peakHour)
    {
        const string habitTitle = "Cheat diet";
        using var fixture = new CompletionFixture("", HttpStatusCode.BadRequest);

        var result = await fixture.Service.GenerateMessageAsync(habitTitle, DayOfWeek.Friday, peakHour, language);

        AssertFallback(result, habitTitle, language);
    }

    [Theory]
    [InlineData("en", 14)]
    [InlineData("en", null)]
    [InlineData("pt-BR", 14)]
    [InlineData("pt-BR", null)]
    public async Task GenerateMessageAsync_BudgetExceeded_ReturnsNeutralFallbackWithoutProviderCall(
        string language, int? peakHour)
    {
        const string habitTitle = "Cheat diet";
        using var fixture = new CompletionFixture(denyBudget: true);

        var result = await fixture.Service.GenerateMessageAsync(habitTitle, DayOfWeek.Friday, peakHour, language);

        AssertFallback(result, habitTitle, language);
        fixture.Handler.RequestCount.Should().Be(0);
        await fixture.Budget.Received(1).ReserveAsync(Arg.Any<Guid>(), 1000L, Arg.Any<CancellationToken>());
        await fixture.Budget.DidNotReceive().SettleAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("fr")]
    [InlineData("pt")]
    [InlineData("pt-BR")]
    [InlineData("PT-BR")]
    public async Task GenerateMessageAsync_Fallback_UsesEstablishedLocaleMapping(string language)
    {
        using var fixture = new CompletionFixture();

        var result = await fixture.Service.GenerateMessageAsync("Smoking", DayOfWeek.Friday, null, language);

        AssertFallback(result, "Smoking", language);
    }

    private static void AssertFallback(Result<(string Title, string Body)> result, string habitTitle, string language)
    {
        result.IsSuccess.Should().BeTrue();
        if (language.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
            result.Value.Should().Be(("Uma escolha de cada vez", "Hoje, você pode seguir o que escolheu."));
        else
            result.Value.Should().Be(("One choice at a time", "You can stick with what you chose for yourself today."));
        AssertNoHabitTitle(result, habitTitle);
        result.Value.Title.Split(' ').Should().HaveCountLessThanOrEqualTo(8);
        var copy = $"{result.Value.Title} {result.Value.Body}";
        foreach (var timeClaim in new[] { "usual time", "horário de costume", "right now", "agora" })
            copy.Should().NotContainEquivalentOf(timeClaim);
    }

    private static void AssertNoHabitTitle(Result<(string Title, string Body)> result, string habitTitle)
    {
        result.Value.Title.Should().NotContainEquivalentOf(habitTitle);
        result.Value.Body.Should().NotContainEquivalentOf(habitTitle);
    }

    private sealed class CompletionFixture : IDisposable
    {
        private readonly HttpClient _httpClient;
        public CannedCompletionHandler Handler { get; }
        public IAiDailyBudget Budget { get; } = Substitute.For<IAiDailyBudget>();
        public AiSlipAlertMessageService Service { get; }

        public CompletionFixture(string content = "", HttpStatusCode status = HttpStatusCode.OK, bool denyBudget = false)
        {
            Handler = new CannedCompletionHandler(content, status);
            _httpClient = new HttpClient(Handler);
            var client = new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
            {
                Endpoint = new Uri("https://orbit.test/v1"),
                Transport = new HttpClientPipelineTransport(_httpClient),
            });
            var completion = new AiCompletionClient(client, NullLogger<AiCompletionClient>.Instance,
                Substitute.For<IAiUsageRecorder>());
            var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "Test"));
            var estimates = Substitute.For<IAiRequestCostEstimator>();
            estimates.EstimateMicroDollarsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(1000L);
            if (denyBudget)
                Budget.ReserveAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromException<Guid>(new AiBudgetExceededException()));
            var budgeted = new BudgetedAiCompletionClient(completion, Budget, http,
                NullLogger<BudgetedAiCompletionClient>.Instance, estimates);
            Service = new AiSlipAlertMessageService(budgeted, NullLogger<AiSlipAlertMessageService>.Instance);
        }

        public void Dispose() => _httpClient.Dispose();
    }

    private sealed class CannedCompletionHandler(string content, HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string> RequestStrings { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            CaptureStrings(document.RootElement);
            return new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(status == HttpStatusCode.OK ? ResponsesTestData.Text(content) : "{}",
                    Encoding.UTF8, "application/json"),
            };
        }

        private void CaptureStrings(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    RequestStrings.Add(element.GetString()!);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        CaptureStrings(item);
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        CaptureStrings(property.Value);
                    break;
            }
        }
    }
}
