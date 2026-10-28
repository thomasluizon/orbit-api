#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenAI.Responses;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.AI;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AiProactiveCheckinMessageServiceTests
{
    public static TheoryData<ProactiveTrigger, string> LocalesAndTriggers
    {
        get
        {
            var data = new TheoryData<ProactiveTrigger, string>();
            foreach (var trigger in Enum.GetValues<ProactiveTrigger>())
                foreach (var locale in new[] { "en", "pt-BR" }) data.Add(trigger, locale);
            return data;
        }
    }

    private static ProactiveFacts Facts(ProactiveTrigger trigger) => trigger switch
    {
        ProactiveTrigger.RestartAfterMiss => new(trigger, [], HadScheduledHabitsYesterday: true,
            HadPositiveLogsYesterday: false, HadRecentActivity: true),
        ProactiveTrigger.BusyCalendarDay => new(trigger, [], TimedEventCount: 4, TimedMinutes: 240),
        ProactiveTrigger.GoalBehind => new(trigger, [], GoalId: Guid.NewGuid(), GoalTitle: "Learn", TrackingStatus: "behind"),
        ProactiveTrigger.WeeklyReview => new(trigger, [], PeriodFrom: new(2030, 10, 7), PeriodTo: new(2030, 10, 13),
            TotalCompletions: 5, TotalScheduled: 7, BadHabitSlips: 0),
        _ => new(trigger, ["Read"], HabitCount: 2, CurrentStreak: 3, HasEstablishedStreak: true)
    };

    [Theory]
    [MemberData(nameof(LocalesAndTriggers))]
    public async Task EveryTriggerAndLocale_HasGroundedTemplateAndLocalizedAction(ProactiveTrigger trigger, string locale)
    {
        var facts = Facts(trigger);
        var service = Service(null);
        var result = await service.GenerateAsync(facts, locale, default);
        result.Text.Length.Should().BeLessThanOrEqualTo(140);
        result.ActionLabel.Should().NotBeNullOrWhiteSpace();
        result.ActionPrompt.Should().NotBeNullOrWhiteSpace();
        AiProactiveCheckinMessageService.IsGrounded(Line(result.Text, facts), facts).Should().BeTrue();
        if (locale == "pt-BR") result.Text.Should().NotContain("pronto");
    }

    [Theory]
    [InlineData("You have 99 habits still open today.")]
    [InlineData("You have -2 habits still open today.")]
    [InlineData("Upgrade your plan today.")]
    [InlineData("Seu plano Pro está pronto hoje.")]
    [InlineData("A habit is open. Pick one.")]
    [InlineData("A habit is open\nPick one.")]
    public async Task UngroundedOrInvalidSentence_UsesWholeTemplate(string candidate)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        var result = await Service(candidate).GenerateAsync(facts, "en", default);
        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Fact]
    public async Task LongSentence_UsesTemplate()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        (await Service(new string('a', 141) + ".").GenerateAsync(facts, "en", default)).Should()
            .Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Theory]
    [InlineData("en", "Read is still open today.")]
    [InlineData("pt-BR", "Você tem 2 hábitos pendentes hoje.")]
    public async Task GroundedSentence_UsesModelWithLowReasoning(string locale, string sentence)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        string? body = null;
        var service = Service(sentence, captureRequest: request => body = request);
        (await service.GenerateAsync(facts, locale, default)).Text.Should().Be(sentence);
        using var document = JsonDocument.Parse(body!);
        var request = document.RootElement;
        request.GetProperty("model").GetString().Should().Be("gpt-6-luna");
        request.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        request.TryGetProperty("temperature", out _).Should().BeFalse();
        request.GetProperty("text").GetProperty("format").GetProperty("type").GetString().Should().Be("json_object");
        body.Should().Contain("feminine").And.Contain("Read");
    }

    [Fact]
    public async Task GroundedMarkdownSentence_ReturnsPlainTextAndPreservesHabitName()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin) with { HabitTitles = ["Read *daily*"] };
        const string text = "**Read *daily*** is __still open__ today.";
        var result = await Service(text, line: Line(text, facts)).GenerateAsync(facts, "en", default);

        result.Text.Should().Be("Read *daily* is still open today.");
    }

    [Theory]
    [InlineData("en", "Read can be your quiet finish this evening, without filling every gap.")]
    [InlineData("pt-BR", "Read pode ser uma escolha tranquila para esta noite.")]
    public async Task NaturalSentence_WithOrdinaryWords_UsesModel(string locale, string sentence)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        var result = await Service(sentence).GenerateAsync(facts, locale, default);

        result.Text.Should().Be(sentence);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task EveningTemplate_NamesOpenHabitsAndSuppliedProgress(string locale)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin) with
        {
            HabitTitles = ["Read", "Walk", "Stretch", "Cook", "Write"],
            HabitCount = 5,
            TotalCompletions = 12
        };

        var result = await Service(null).GenerateAsync(facts, locale, default);

        result.Text.Should().Contain("Read").And.Contain("Walk").And.Contain("Stretch");
        result.Text.Should().Contain("12").And.Contain("2");
        result.Text.Should().NotContain("Cook").And.NotContain("Write");
        result.Text.Length.Should().BeLessThanOrEqualTo(ProactiveRules.MaxTextLength);
    }

    [Fact]
    public async Task ModelFailure_UsesTemplate()
    {
        var facts = Facts(ProactiveTrigger.RestartAfterMiss);
        var service = Service(null, fail: true);
        (await service.GenerateAsync(facts, "pt-BR", default)).Should().Be(AiProactiveCheckinMessageService.Template(facts, "pt-BR"));
    }

    [Theory]
    [InlineData("Read and Swim are still open today.", "Read|Swim", "")]
    [InlineData("Read and Return are still open today.", "Read|Return", "")]
    [InlineData("Your goal Marathon needs attention today.", "", "Marathon")]
    [InlineData("Your goal Home needs attention today.", "", "Home")]
    [InlineData("Leitura e Natação cabem nesta noite.", "Leitura|Natação", "")]
    public async Task InventedTitle_InStructuredModelOutput_UsesWholeTemplate(string text, string habits, string goals)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        var line = new AiProactiveCheckinMessageService.ProactiveLine(text,
            habits.Split('|', StringSplitOptions.RemoveEmptyEntries), goals.Split('|', StringSplitOptions.RemoveEmptyEntries));

        var result = await Service(text, line: line).GenerateAsync(facts, "en", default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Fact]
    public async Task MissingTitleDeclarations_UsesWholeTemplate()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        var result = await Service("Read fits tonight.",
            line: new("Read fits tonight.", null, null)).GenerateAsync(facts, "en", default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task LongLiteralTitles_KeepFallbackWithinLimitWithoutInventingShortNames(string locale)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin) with { HabitTitles = [new string('a', 200)], HabitCount = 1 };

        var result = await Service(null).GenerateAsync(facts, locale, default);

        result.Text.Length.Should().BeLessThanOrEqualTo(ProactiveRules.MaxTextLength);
        result.Text.Should().Contain("1");
        result.Text.Should().NotContain("aaaa");
    }

    [Theory]
    [InlineData("en", "yesterday")]
    [InlineData("pt-BR", "ontem")]
    public async Task RestartTemplate_RefersToMissOnlyWhenConfirmed(string locale, string yesterday)
    {
        var facts = Facts(ProactiveTrigger.RestartAfterMiss);
        (await Service(null).GenerateAsync(facts, locale, default)).Text.Should().Contain(yesterday);
        var unconfirmed = facts with { HadPositiveLogsYesterday = null };
        (await Service(null).GenerateAsync(unconfirmed, locale, default)).Text.Should().NotContain(yesterday);
    }

    [Fact]
    public async Task LiteralTitleNumbersAndPunctuation_DoNotGroundUnrelatedFigures()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin) with { HabitTitles = ["Read 99 pages. Plan"], HabitCount = 1 };
        const string valid = "Read 99 pages. Plan can be your quiet finish tonight.";
        (await Service(valid, line: Line(valid, facts)).GenerateAsync(facts, "en", default)).Text.Should().Be(valid);
        const string invented = "Read 99 pages. Plan and 99 more habits are still open.";
        (await Service(invented, line: Line(invented, facts)).GenerateAsync(facts, "en", default)).Should()
            .Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    private static AiProactiveCheckinMessageService.ProactiveLine Line(string? text, ProactiveFacts facts) => new(text,
        facts.HabitTitles.Where(title => text?.Contains(title, StringComparison.Ordinal) == true).ToArray(),
        facts.GoalTitle is not null && text?.Contains(facts.GoalTitle, StringComparison.Ordinal) == true ? [facts.GoalTitle] : []);

    private static AiProactiveCheckinMessageService Service(string? text,
        AiProactiveCheckinMessageService.ProactiveLine? line = null, Action<string>? captureRequest = null, bool fail = false)
    {
        var client = new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = new Uri("https://orbit.test/v1"),
            RetryPolicy = new ClientRetryPolicy(0),
            Transport = new HttpClientPipelineTransport(new HttpClient(new ResponseHandler(
                JsonSerializer.Serialize(line ?? Line(text, Facts(ProactiveTrigger.EveningCheckin))), captureRequest, fail)))
        });
        var completion = new AiCompletionClient(client, NullLogger<AiCompletionClient>.Instance, Substitute.For<IAiUsageRecorder>());
        return new(AiBudgetTestDouble.Wrap(completion), NullLogger<AiProactiveCheckinMessageService>.Instance);
    }

    private sealed class ResponseHandler(string content, Action<string>? captureRequest, bool fail) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (fail) throw new HttpRequestException("Unavailable");
            captureRequest?.Invoke(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(ResponsesTestData.Text(content), Encoding.UTF8, "application/json")
            };
        }
    }
}
