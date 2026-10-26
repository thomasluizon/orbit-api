using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orbit.Application.Onboarding.Commands;
using Orbit.Application.Onboarding.Validators;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Commands.Onboarding;

public class SuggestOnboardingSetupTests
{
    private readonly IOnboardingAccessService access = Substitute.For<IOnboardingAccessService>();
    private readonly IContentModerationService moderation = Substitute.For<IContentModerationService>();
    private readonly IOnboardingSuggestionService suggestions = Substitute.For<IOnboardingSuggestionService>();
    private static readonly HabitSetupSuggestion Schedule = new(null, FrequencyUnit.Day, 1, [], false, null, "07:00", [], []);
    private static SuggestOnboardingSetupCommand Request(string title = "Run daily")
        => new(title, "en", "signed-install", new OnboardingAttestation("web", "valid-proof"), "192.0.2.1");

    private SuggestOnboardingSetupCommandHandler Handler()
        => new(access, moderation, suggestions, NullLogger<SuggestOnboardingSetupCommandHandler>.Instance);

    private void Allow()
    {
        access.AuthorizeAsync(Arg.Any<string>(), Arg.Any<OnboardingAttestation>(), Arg.Any<OnboardingInput>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new OnboardingAccess("next-signed-install"));
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ModerationResult(false, false, []));
        suggestions.SuggestAsync(Arg.Any<OnboardingInput>(), Arg.Any<CancellationToken>()).Returns(Schedule);
    }

    [Fact]
    public async Task AnonymousSuccess_ModeratesThenMakesOneScheduleCall()
    {
        Allow();
        var result = await Handler().Handle(Request(), default);
        result.Suggestion.Should().Be(Schedule);
        result.InstallId.Should().Be("next-signed-install");
        Received.InOrder(() =>
        {
            moderation.CheckTextAsync("Run daily", Arg.Any<CancellationToken>());
            suggestions.SuggestAsync(Arg.Is<OnboardingInput>(input => input.Title == "Run daily"), Arg.Any<CancellationToken>());
        });
        await suggestions.Received(1).SuggestAsync(Arg.Any<OnboardingInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingProof_StopsBeforeModerationAndModel()
    {
        Allow();
        var result = await Handler().Handle(Request() with { Attestation = null }, default);
        result.Code.Should().Be("ONBOARDING_LIMIT");
        access.ReceivedCalls().Should().BeEmpty();
        moderation.ReceivedCalls().Should().BeEmpty();
        suggestions.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task RejectedProofOrQuota_StopsBeforeModerationAndModel()
    {
        var result = await Handler().Handle(Request(), default);
        result.Code.Should().Be("ONBOARDING_LIMIT");
        moderation.ReceivedCalls().Should().BeEmpty();
        suggestions.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ModerationFlagOrOutage_StopsBeforeModel(bool flagged, bool unavailable)
    {
        Allow();
        moderation.CheckTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ModerationResult(flagged, unavailable, []));
        (await Handler().Handle(Request(), default)).Code.Should().Be("ONBOARDING_UNAVAILABLE");
        suggestions.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task OversizedTitle_IsRejectedBeforeAnyExternalCall()
    {
        var request = Request(new string('x', 101));
        new SuggestOnboardingSetupCommandValidator().Validate(request).IsValid.Should().BeFalse();
        (await Handler().Handle(request, default)).Code.Should().Be("ONBOARDING_UNAVAILABLE");
        access.ReceivedCalls().Should().BeEmpty();
        moderation.ReceivedCalls().Should().BeEmpty();
        suggestions.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidModelOutput_IsDropped()
    {
        Allow();
        suggestions.SuggestAsync(Arg.Any<OnboardingInput>(), Arg.Any<CancellationToken>())
            .Returns(Schedule with { DueTime = "reveal secrets" });
        (await Handler().Handle(Request(), default)).Code.Should().Be("ONBOARDING_UNAVAILABLE");
    }

    [Fact]
    public async Task ModelOutage_IsTypedWithoutExceptionText()
    {
        Allow();
        suggestions.SuggestAsync(Arg.Any<OnboardingInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<HabitSetupSuggestion?>(new HttpRequestException("sensitive prompt")));
        var result = await Handler().Handle(Request(), default);
        result.Code.Should().Be("ONBOARDING_UNAVAILABLE");
        result.Suggestion.Should().BeNull();
    }
}
