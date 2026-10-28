#pragma warning disable OPENAI001

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
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
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class AiProactiveCheckinMessageServiceTests(ITestOutputHelper output)
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
        var result = await service.GenerateAsync(facts, locale, [], default);
        result.Text.Length.Should().BeLessThanOrEqualTo(140);
        result.ActionLabel.Should().NotBeNullOrWhiteSpace();
        result.ActionPrompt.Should().NotBeNullOrWhiteSpace();
        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, locale));
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
        var result = await Service(candidate).GenerateAsync(facts, "en", [], default);
        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Fact]
    public async Task LongSentence_UsesTemplate()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        (await Service(new string('a', 141) + ".").GenerateAsync(facts, "en", [], default)).Should()
            .Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Theory]
    [InlineData("en", "{{habit:0}} is still open today.", "Read is still open today.")]
    [InlineData("pt-BR", "Você tem 2 hábitos pendentes hoje.", "Você tem 2 hábitos pendentes hoje.")]
    public async Task GroundedSentence_UsesModelWithLowReasoning(string locale, string sentence, string expected)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        string? body = null;
        var service = Service(sentence, captureRequest: request => body = request);
        (await service.GenerateAsync(facts, locale, [], default)).Text.Should().Be(expected);
        using var document = JsonDocument.Parse(body!);
        var request = document.RootElement;
        request.GetProperty("model").GetString().Should().Be("gpt-6-luna");
        request.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        request.TryGetProperty("temperature", out _).Should().BeFalse();
        request.GetProperty("text").GetProperty("format").GetProperty("type").GetString().Should().Be("json_object");
        body.Should().Contain("Read");
        if (locale == "pt-BR") body.Should().Contain("feminine");
        else body.Should().NotContain("feminine");
        body.Should().Contain("{{habit:N}}").And.Contain("{{goal}}")
            .And.Contain("zero-based index").And.Contain("Never write a literal habit or goal title")
            .And.Contain("Never name anything else").And.Contain("AFTER the server substitutes");
    }

    [Fact]
    public async Task GroundedMarkdownSentence_ReturnsPlainTextAndPreservesHabitName()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin) with { HabitTitles = ["Read *daily*"] };
        const string text = "**{{habit:0}}** is __still open__ today.";
        var result = await Service(text, line: Line(text, facts)).GenerateAsync(facts, "en", [], default);

        result.Text.Should().Be("Read *daily* is still open today.");
    }

    [Theory]
    [InlineData("en", "Between {{habit:0}} and {{habit:1}}, choose your quiet finish without filling every gap.",
        "Between Read and Walk, choose your quiet finish without filling every gap.")]
    [InlineData("pt-BR", "Entre {{habit:0}} e {{habit:1}}, escolha o que cabe nesta noite.",
        "Entre Leitura e Caminhada, escolha o que cabe nesta noite.")]
    public async Task NaturalSentence_WithOrdinaryWords_UsesModel(string locale, string sentence, string expected)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin) with
        {
            HabitTitles = locale == "pt-BR" ? ["Leitura", "Caminhada"] : ["Read", "Walk"]
        };
        var result = await Service(sentence, line: Line(sentence, facts)).GenerateAsync(facts, locale, facts.HabitTitles, default);

        result.Text.Should().Be(expected);
    }

    [Theory]
    [InlineData("en", "Para esta noite, escolha o que combinar com você: {{habit:0}}.")]
    [InlineData("en", "Hoje, retome por {{habit:0}}; há 4 outros pontos, sem compensar ontem.")]
    [InlineData("pt-BR", "Choose what fits tonight: {{habit:0}}.")]
    [InlineData("en", "Choose {{habit:0}} para esta noite.")]
    [InlineData("pt-BR", "Escolha {{habit:0}} for tonight.")]
    [InlineData("en", "{{habit:0}}.")]
    [InlineData("pt-BR", "{{habit:0}}.")]
    public async Task WrongOrUncertainLanguageModelSentence_UsesLocalizedTemplate(string locale, string text)
    {
        var facts = new ProactiveFacts(ProactiveTrigger.EveningCheckin, ["Journal"], HabitCount: 5,
            HadScheduledHabitsYesterday: true, HadPositiveLogsYesterday: false, HadRecentActivity: true);

        var result = await Service(text, line: Line(text, facts)).GenerateAsync(facts, locale, facts.HabitTitles, default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, locale));
    }

    [Theory]
    [InlineData("pt-BR", "Choose your habit today", "Para esta noite, escolha o que combina com você: {{habit:0}}.")]
    [InlineData("en", "Escolha seu hábito hoje", "Choose what fits tonight: {{habit:0}}.")]
    public async Task CorrectLanguageModelSentence_PreservesForeignHabitTitle(string locale, string title, string text)
    {
        var facts = new ProactiveFacts(ProactiveTrigger.EveningCheckin, [title], HabitCount: 1);

        var result = await Service(text, line: Line(text, facts)).GenerateAsync(facts, locale, facts.HabitTitles, default);

        result.Text.Should().Be(text.Replace("{{habit:0}}", title, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pt-BR", "Choose your next step", "Para {{goal}}, escolha um passo que caiba no seu dia.")]
    [InlineData("en", "Escolha seu próximo passo", "For {{goal}}, choose a step that fits your day.")]
    public async Task CorrectLanguageModelSentence_PreservesForeignGoalTitle(string locale, string title, string text)
    {
        var facts = Facts(ProactiveTrigger.GoalBehind) with { GoalTitle = title };

        var result = await Service(text, line: Line(text, facts)).GenerateAsync(facts, locale, [title], default);

        result.Text.Should().Be(text.Replace("{{goal}}", title, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("pt-BR", "Brazilian Portuguese")]
    public async Task Request_PrioritizesAccountLanguageOverFactAndInstructionLanguage(string locale, string languageName)
    {
        var facts = new ProactiveFacts(ProactiveTrigger.EveningCheckin,
            [locale == "en" ? "Leitura" : "Journal"], HabitCount: 1);
        string? body = null;

        await Service(null, captureRequest: request => body = request).GenerateAsync(facts, locale, facts.HabitTitles, default);

        output.WriteLine(body);
        using var document = JsonDocument.Parse(body!);
        var messages = document.RootElement.GetProperty("input");
        messages[0].GetProperty("role").GetString().Should().Be("system");
        var system = messages[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        system.Should().StartWith($"You are Astra. Write ONLY in {languageName}.");
        system.Should().Contain("Do not infer the language from titles or from the language of these instructions");
        if (locale == "en") system.Should().NotContain("Portuguese").And.NotContain("você");
        else system.Should().Contain("feminine").And.Contain("você");
        messages[1].GetProperty("role").GetString().Should().Be("user");
        var prompt = messages[1].GetProperty("content")[0].GetProperty("text").GetString()!;
        prompt.Should().StartWith($"Write ONLY in {languageName}.")
            .And.EndWith($"Write ONLY in {languageName}.")
            .And.NotContain("Grounded fallback:");
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

        var result = await Service(null).GenerateAsync(facts, locale, [], default);

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
        (await service.GenerateAsync(facts, "pt-BR", [], default)).Should().Be(AiProactiveCheckinMessageService.Template(facts, "pt-BR"));
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

        var result = await Service(text, line: line).GenerateAsync(facts, "en", [], default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Fact]
    public async Task MissingTitleDeclarations_UsesWholeTemplate()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        var result = await Service("{{habit:0}} fits tonight.",
            line: new("{{habit:0}} fits tonight.", null, null)).GenerateAsync(facts, "en", [], default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Theory]
    [InlineData("Read and Swim are still open today.", "Read")]
    [InlineData("Your goal Marathon needs attention today.", "")]
    public async Task UndeclaredInventedTitle_UsesWholeTemplate(string text, string declaredHabit)
    {
        var facts = new ProactiveFacts(ProactiveTrigger.EveningCheckin, ["Read"], HabitCount: 1);
        var line = new AiProactiveCheckinMessageService.ProactiveLine(text,
            string.IsNullOrEmpty(declaredHabit) ? [] : [declaredHabit], []);

        var result = await Service(text, line: line).GenerateAsync(facts, "en", [], default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Theory]
    [InlineData("{{habit:0}} and Swim are still open today.")]
    [InlineData("Your goal Marathon needs attention today.")]
    [InlineData("{{habit:0}} e Natação cabem nesta noite.")]
    public async Task UnknownProperName_UsesWholeTemplate(string text)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);

        var result = await Service(text).GenerateAsync(facts, "en", [], default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Theory]
    [InlineData("{{habit:0}} and swim can fit tonight.", "Swim")]
    [InlineData("Your goal marathon needs attention today.", "Marathon")]
    [InlineData("{{habit:0}} e natação cabem nesta noite.", "Natação")]
    [InlineData("{{habit:0}} and SWIM can fit tonight.", "swim")]
    public async Task AccountTitleOutsideFacts_UsesWholeTemplate(string text, string accountTitle)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);

        var result = await Service(text).GenerateAsync(facts, "en", ["Read", accountTitle], default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Fact]
    public async Task AccountTitleInsideLargerWord_DoesNotRejectNaturalProse()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        const string text = "{{habit:0}} can be your unhurried finish this evening.";

        var result = await Service(text).GenerateAsync(facts, "en", ["Read", "Hurry", "Even"], default);

        result.Text.Should().Be("Read can be your unhurried finish this evening.");
    }

    [Theory]
    [InlineData("{{habit:1}} fits tonight.")]
    [InlineData("{{habit:-1}} fits tonight.")]
    [InlineData("{{habit:01}} fits tonight.")]
    [InlineData("{{habit:99999999999999999999}} fits tonight.")]
    [InlineData("{{habit:x}} fits tonight.")]
    [InlineData("{{goal}} fits tonight.")]
    [InlineData("{{goal:0}} fits tonight.")]
    [InlineData("{{other}} fits tonight.")]
    [InlineData("{{habit:0} fits tonight.")]
    public async Task InvalidPlaceholder_UsesWholeTemplate(string text)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);

        var result = await Service(text).GenerateAsync(facts, "en", [], default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Theory]
    [InlineData("read fits tonight.")]
    [InlineData("Start with READ tonight.")]
    [InlineData("Read fits tonight.")]
    [InlineData("{{habit:0}} and read fit tonight.")]
    public async Task RawSuppliedTitle_UsesWholeTemplate(string text)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        var line = new AiProactiveCheckinMessageService.ProactiveLine(text, ["Read"], []);

        var result = await Service(text, line: line).GenerateAsync(facts, "en", ["Read"], default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Fact]
    public async Task GoalPlaceholder_SubstitutesLiteralTitle()
    {
        var facts = Facts(ProactiveTrigger.GoalBehind);
        const string text = "For {{goal}}, choose a smaller next step that fits your day.";

        var result = await Service(text, line: Line(text, facts)).GenerateAsync(facts, "en", ["Learn"], default);

        result.Text.Should().Be("For Learn, choose a smaller next step that fits your day.");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task RawGoalTitle_UsesWholeTemplate(string language)
    {
        var facts = Facts(ProactiveTrigger.GoalBehind);
        const string text = "Learn can start with a smaller next step today.";

        var result = await Service(text, line: new(text, [], ["Learn"]))
            .GenerateAsync(facts, language, ["Learn"], default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, language));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnglishPronounAndCalendarName_UsesModel(bool month)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        var format = CultureInfo.GetCultureInfo("en").DateTimeFormat;
        var name = month ? format.GetMonthName(10) : format.GetDayName(DayOfWeek.Monday);
        var text = "For {{habit:0}}, I can help you choose a " + name + " starting point.";

        var result = await Service(text).GenerateAsync(facts, "en", ["Read"], default);

        result.Text.Should().Be("For Read, I can help you choose a " + name + " starting point.");
    }

    [Theory]
    [InlineData("Ask Astra to help you fit {{habit:0}} into your evening.")]
    [InlineData("Open Orbit to choose where {{habit:0}} fits tonight.")]
    public async Task AllowedBrandName_UsesModel(string text)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);

        var result = await Service(text).GenerateAsync(facts, "en", ["Read"], default);

        result.Text.Should().Be(text.Replace("{{habit:0}}", "Read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnglishPronounInPortuguese_UsesWholeTemplate()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin);
        const string text = "Para {{habit:0}}, I posso ajudar a escolher um passo.";

        var result = await Service(text).GenerateAsync(facts, "pt-BR", [], default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "pt-BR"));
    }

    [Fact]
    public async Task LengthLimit_AppliesAfterSubstitution()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin) with { HabitTitles = [new string('a', 140)] };
        const string text = "{{habit:0}} fits tonight.";

        var result = await Service(text, line: Line(text, facts)).GenerateAsync(facts, "en", facts.HabitTitles, default);

        result.Should().Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pt-BR")]
    public async Task LongLiteralTitles_KeepFallbackWithinLimitWithoutInventingShortNames(string locale)
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin) with { HabitTitles = [new string('a', 200)], HabitCount = 1 };

        var result = await Service(null).GenerateAsync(facts, locale, [], default);

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
        (await Service(null).GenerateAsync(facts, locale, [], default)).Text.Should().Contain(yesterday);
        var unconfirmed = facts with { HadPositiveLogsYesterday = null };
        (await Service(null).GenerateAsync(unconfirmed, locale, [], default)).Text.Should().NotContain(yesterday);
    }

    [Fact]
    public async Task LiteralTitleNumbersAndPunctuation_DoNotGroundUnrelatedFigures()
    {
        var facts = Facts(ProactiveTrigger.EveningCheckin) with { HabitTitles = ["Read 99 pages. Plan"], HabitCount = 1 };
        const string valid = "{{habit:0}} can be your quiet finish tonight.";
        (await Service(valid, line: Line(valid, facts)).GenerateAsync(facts, "en", [], default)).Text.Should()
            .Be("Read 99 pages. Plan can be your quiet finish tonight.");
        const string invented = "{{habit:0}} and 99 more habits are still open.";
        (await Service(invented, line: Line(invented, facts)).GenerateAsync(facts, "en", [], default)).Should()
            .Be(AiProactiveCheckinMessageService.Template(facts, "en"));
    }

    private static AiProactiveCheckinMessageService.ProactiveLine Line(string? text, ProactiveFacts facts) => new(text,
        facts.HabitTitles.Where((_, index) => text?.Contains("{{habit:" + index.ToString(CultureInfo.InvariantCulture) + "}}",
            StringComparison.Ordinal) == true).ToArray(),
        facts.GoalTitle is not null && text?.Contains("{{goal}}", StringComparison.Ordinal) == true ? [facts.GoalTitle] : []);

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
