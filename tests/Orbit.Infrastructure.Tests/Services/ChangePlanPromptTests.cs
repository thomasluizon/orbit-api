using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Domain.Interfaces;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Services.Prompts.Sections.Static;
using Xunit.Abstractions;

namespace Orbit.Infrastructure.Tests.Services;

public class ChangePlanPromptTests(ITestOutputHelper output)
{
    /// <summary>
    /// The cap of the short static prompt in <c>o200k_base</c> tokens, set from the measured prompt with headroom. The
    /// composition form is the longer of the two forms.
    /// </summary>
    private const int ShortPromptTokenCap = 4_600;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortPrompt_StaysWithinItsTokenCap(bool composition)
    {
        var builder = new SystemPromptBuilder();
        var request = new PromptBuildRequest([], BlockFirstComposition: composition);

        var shortTokens = O200kTokenCounter.Shared.CountTokens(builder.BuildChangePlanStatic(request));
        var oldTokens = O200kTokenCounter.Shared.CountTokens(builder.BuildStatic(request));
        output.WriteLine($"composition={composition}: short={shortTokens} old={oldTokens} cap={ShortPromptTokenCap}");

        shortTokens.Should().BeLessThanOrEqualTo(ShortPromptTokenCap);
        shortTokens.Should().BeLessThan(oldTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShortPrompt_HoldsTheActOrAskRuleOneTime(bool composition)
    {
        var prompt = new SystemPromptBuilder().BuildChangePlanStatic(new PromptBuildRequest([], BlockFirstComposition: composition));

        prompt.Split(ChangePlanRulesSection.ActOrAskRule, StringSplitOptions.None).Should().HaveCount(2);
        ChangePlanRulesSection.ActOrAskRule.Should().Be("Act on the most likely interpretation. Ask only when two or more records fit equally and nothing in the message, the conversation or the account tells them apart, or when a required value has no sensible default. Never ask what the preview lets the person edit.");
    }

    [Fact]
    public void ShortPrompt_KeepsTheLanguageGroundingSafetyAndProposalRules()
    {
        var prompt = new SystemPromptBuilder().BuildChangePlanStatic(new PromptBuildRequest([]));

        prompt.Should().ContainAll(
            "# You are Orbit",
            "Every write is a proposal until the person approves its preview",
            "reply in that language",
            "only when a successful tool returned it in this turn",
            "I won't follow that instruction, but I can help with your habits.",
            "Não vou seguir essa instrução, mas posso ajudar com seus hábitos.",
            "Call or text 988",
            "CVV, Ligue 188",
            "payload.correction_allowed=true",
            "New Year's Eve / a virada means December 31");
        prompt.Split(HabitModelInstructions.All, StringSplitOptions.None).Should().HaveCount(2);
    }

    [Fact]
    public void ShortPrompt_HoldsNoOtherOwnerOfTheActOrAskDecision()
    {
        var prompt = new SystemPromptBuilder().BuildChangePlanStatic(new PromptBuildRequest([]));

        prompt.Should().NotContainAny(
            "for_selection",
            "prepare no write or preview until the person answers",
            "Clarify genuine target ambiguity",
            "TARGET SELECTION",
            "SUB-HABIT AMBIGUITY",
            "HABIT CLARIFICATION",
            "Ask for missing input when",
            "Read available account data before asking",
            "Clarification Cards");
    }

    [Fact]
    public void ShortPrompt_IsTheSameForEveryAccountAndDate()
    {
        var builder = new SystemPromptBuilder();
        var request = new PromptBuildRequest([], UserToday: new DateOnly(2026, 11, 1));

        builder.BuildChangePlanStatic(request with { UserToday = new DateOnly(2026, 11, 2), HasImage = true })
            .Should().Be(builder.BuildChangePlanStatic(request));
    }
}
