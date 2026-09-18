using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Prompts;

namespace Orbit.Infrastructure.Tests.Services;

/// <summary>
/// The register is pinned in the prompt rather than in a post-processing scrub, so
/// these tests assert the constraint each prompt carries, never a generated wording.
/// They also assert that no prompt demonstrates what it bans: the prompts these
/// replaced taught an exclamation mark and a doubled hyphen by worked example, and
/// at temperature 0.9 the model reproduced both.
/// </summary>
public class NotificationVoiceTests
{
    private const string DoubledHyphen = "--";
    /// <summary>
    /// The two banned dashes are built from their code points instead of typed, because a file
    /// that detects a dash by embedding one still embeds one, and the Dash Ban gate scans this
    /// file like any other. U+2014 is the em dash and U+2013 is the en dash.
    /// </summary>
    private static readonly string EmDash = ((char)0x2014).ToString();

    private static readonly string EnDash = ((char)0x2013).ToString();

    private static readonly string[] HypeWords =
    [
        "unlock", "elevate", "empower", "supercharge", "leverage", "harness", "delve",
        "seamless", "robust", "crucial", "essential", "vital", "revolutionary", "maximize", "optimize",
    ];

    public static TheoryData<string, string> EveryPromptSentToAModel() => new()
    {
        { "slip alert, system", AiSlipAlertMessageService.SystemPrompt },
        { "slip alert, en", AiSlipAlertMessageService.BuildPrompt("Smoking", DayOfWeek.Friday, 14, "en") },
        { "slip alert, pt-BR", AiSlipAlertMessageService.BuildPrompt("Fumar", DayOfWeek.Monday, null, "pt-BR") },
        { "proactive checkin, system", AiProactiveCheckinMessageService.SystemPrompt },
        { "proactive checkin, en", AiProactiveCheckinMessageService.BuildPrompt("Thomas", ["Run"], 4, "en") },
        { "proactive checkin, pt-BR", AiProactiveCheckinMessageService.BuildPrompt("Thomas", ["Correr"], 0, "pt-BR") },
        { "daily summary, system", AiSummaryService.SystemPrompt },
        { "daily summary, en", DailySummaryPrompt("en") },
        { "daily summary, pt-BR", DailySummaryPrompt("pt-BR") },
    };

    public static TheoryData<string, string> EveryGeneratedUserPrompt() => new()
    {
        { "slip alert, en", AiSlipAlertMessageService.BuildPrompt("Smoking", DayOfWeek.Friday, 14, "en") },
        { "slip alert, pt-BR", AiSlipAlertMessageService.BuildPrompt("Fumar", DayOfWeek.Monday, null, "pt-BR") },
        { "proactive checkin, en", AiProactiveCheckinMessageService.BuildPrompt("Thomas", ["Run"], 4, "en") },
        { "proactive checkin, pt-BR", AiProactiveCheckinMessageService.BuildPrompt("Thomas", ["Correr"], 0, "pt-BR") },
        { "daily summary, en", DailySummaryPrompt("en") },
        { "daily summary, pt-BR", DailySummaryPrompt("pt-BR") },
    };

    /// <summary>
    /// The daily summary is built rather than declared, and it is the prompt this pull request
    /// strips doubled hyphens from, so the guard written for that leak has to reach it.
    /// </summary>
    private static string DailySummaryPrompt(string language)
    {
        var today = new DateOnly(2026, 9, 18);
        var habit = Habit.Create(new HabitCreateParams(
            Guid.NewGuid(),
            language == "pt-BR" ? "Correr" : "Run",
            FrequencyUnit.Day,
            1,
            DueDate: today)).Value;

        return AiSummaryService.BuildSummaryPrompt(
            [habit],
            new DailySummaryContext(today, today, today, language, null, 0, 0, new Dictionary<Guid, DateOnly>()));
    }

    [Theory]
    [MemberData(nameof(EveryPromptSentToAModel))]
    public void NoPromptDemonstratesACharacterItBans(string label, string prompt)
    {
        prompt.Should().NotContain("!", $"the {label} prompt must not model an exclamation mark");
        prompt.Should().NotContain(DoubledHyphen, $"the {label} prompt must not model a doubled hyphen");
        prompt.Should().NotContain(EmDash, $"the {label} prompt must not model an em dash");
        prompt.Should().NotContain(EnDash, $"the {label} prompt must not model an en dash");
    }

    [Theory]
    [MemberData(nameof(EveryGeneratedUserPrompt))]
    public void EveryUserPromptCarriesTheSharedVoiceContract(string label, string prompt)
    {
        prompt.Should().Contain(NotificationVoice.Rules, $"the {label} prompt must pin the register in the prompt");
    }

    [Theory]
    [MemberData(nameof(EveryGeneratedUserPrompt))]
    public void EveryUserPromptNamesTheLanguageItMustWriteIn(string label, string prompt)
    {
        var languageName = label.EndsWith("pt-BR", StringComparison.Ordinal) ? "Brazilian Portuguese" : "English";

        prompt.Should().Contain($"Write ONLY in {languageName}");
    }

    /// <summary>
    /// Asserted over every prompt a model actually receives rather than over
    /// <c>NotificationVoice.Rules</c> itself. A test that reads a constant's own literals back out
    /// of it only reddens when somebody edits that constant, which no product change does. These
    /// redden when a generator stops carrying the contract, which is the failure that ships slop.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryGeneratedUserPrompt))]
    public void EveryPromptTellsTheModelWhichCharactersAreBanned(string label, string prompt)
    {
        foreach (var ban in new[]
                 {
                     "no exclamation mark", "no em dash", "no en dash",
                     "no doubled hyphen", "no emoji", "no markdown",
                 })
        {
            prompt.Should().Contain(ban, $"the {label} prompt must ban {ban}");
        }
    }

    /// <inheritdoc cref="EveryPromptTellsTheModelWhichCharactersAreBanned"/>
    [Theory]
    [MemberData(nameof(EveryGeneratedUserPrompt))]
    public void EveryPromptBansTheSellingRegister(string label, string prompt)
    {
        foreach (var ban in new[]
                 {
                     "Never sell", "never perform enthusiasm", "Never shame, blame, scold or warn",
                     "Never call them the user", "Never frame anything as a journey",
                 })
        {
            prompt.Should().Contain(ban, $"the {label} prompt must carry: {ban}");
        }
    }

    /// <inheritdoc cref="EveryPromptTellsTheModelWhichCharactersAreBanned"/>
    [Theory]
    [MemberData(nameof(EveryPromptAndHypeWord))]
    public void EveryPromptNamesEveryBannedHypeWord(string label, string prompt, string hypeWord)
    {
        prompt.Should().Contain(
            hypeWord, $"the {label} prompt can only keep \"{hypeWord}\" out if it names it");
    }

    public static TheoryData<string, string, string> EveryPromptAndHypeWord()
    {
        var cases = new TheoryData<string, string, string>();
        foreach (var promptCase in EveryGeneratedUserPrompt())
        {
            var label = (string)promptCase[0];
            var prompt = (string)promptCase[1];
            foreach (var word in HypeWords)
                cases.Add(label, prompt, word);
        }
        return cases;
    }
}
