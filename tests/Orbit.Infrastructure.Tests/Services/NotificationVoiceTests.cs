using FluentAssertions;
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
    private const string EmDash = "—";
    private const string EnDash = "–";

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
    };

    public static TheoryData<string, string> EveryGeneratedUserPrompt() => new()
    {
        { "slip alert, en", AiSlipAlertMessageService.BuildPrompt("Smoking", DayOfWeek.Friday, 14, "en") },
        { "slip alert, pt-BR", AiSlipAlertMessageService.BuildPrompt("Fumar", DayOfWeek.Monday, null, "pt-BR") },
        { "proactive checkin, en", AiProactiveCheckinMessageService.BuildPrompt("Thomas", ["Run"], 4, "en") },
        { "proactive checkin, pt-BR", AiProactiveCheckinMessageService.BuildPrompt("Thomas", ["Correr"], 0, "pt-BR") },
    };

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

    [Fact]
    public void TheVoiceContractBansEveryCharacterClassTheOldPromptsLeaked()
    {
        NotificationVoice.Rules.Should().Contain("no exclamation mark");
        NotificationVoice.Rules.Should().Contain("no em dash");
        NotificationVoice.Rules.Should().Contain("no en dash");
        NotificationVoice.Rules.Should().Contain("no doubled hyphen");
        NotificationVoice.Rules.Should().Contain("no emoji");
        NotificationVoice.Rules.Should().Contain("no markdown");
    }

    [Fact]
    public void TheVoiceContractBansTheSellingRegister()
    {
        NotificationVoice.Rules.Should().Contain("Never sell");
        NotificationVoice.Rules.Should().Contain("never perform enthusiasm");
        NotificationVoice.Rules.Should().Contain("Never shame, blame, scold or warn");
        NotificationVoice.Rules.Should().Contain("Never call them the user");
        NotificationVoice.Rules.Should().Contain("Never frame anything as a journey");
    }

    [Theory]
    [MemberData(nameof(HypeWordCases))]
    public void TheVoiceContractNamesEveryBannedHypeWord(string hypeWord)
    {
        NotificationVoice.Rules.Should().Contain(hypeWord, $"the model can only avoid \"{hypeWord}\" if it is told to");
    }

    public static TheoryData<string> HypeWordCases()
    {
        var cases = new TheoryData<string>();
        foreach (var word in HypeWords)
            cases.Add(word);
        return cases;
    }
}
