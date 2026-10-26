using FluentAssertions;
using Orbit.Application.Onboarding.Commands;
using Orbit.Application.Onboarding.Validators;
using Orbit.Domain.Models;

namespace Orbit.Application.Tests.Validators;

public class OnboardingValidatorsTests
{
    [Fact]
    public void SetupValidator_EnforcesBoundedTitleLanguageAndEvidence()
    {
        var validator = new SuggestOnboardingSetupCommandValidator();
        var request = new SuggestOnboardingSetupCommand("Read", "pt-BR", "signed-id", new("ios", "token", "key"), "ip");
        validator.Validate(request).IsValid.Should().BeTrue();
        validator.Validate(request with { Title = new string('x', 101) }).IsValid.Should().BeFalse();
        validator.Validate(request with { Language = "instructions" }).IsValid.Should().BeFalse();
        validator.Validate(request with { InstallId = new string('x', 4097) }).IsValid.Should().BeFalse();
        validator.Validate(request with { Attestation = new("unknown", "token") }).IsValid.Should().BeFalse();
        validator.Validate(request with { Attestation = new("ios", new string('x', 32769)) }).IsValid.Should().BeFalse();
    }

    [Fact]
    public void EnrollmentValidators_EnforcePlatformChallengeAndTokenBounds()
    {
        var challenge = new GetOnboardingChallengeCommandValidator();
        challenge.Validate(new GetOnboardingChallengeCommand("web", "ip")).IsValid.Should().BeTrue();
        challenge.Validate(new GetOnboardingChallengeCommand("unknown", "ip")).IsValid.Should().BeFalse();
        var enrollment = new EnrollOnboardingInstallCommandValidator();
        enrollment.Validate(new EnrollOnboardingInstallCommand("challenge", new("android", "token"), "ip")).IsValid.Should().BeTrue();
        enrollment.Validate(new EnrollOnboardingInstallCommand("", new("android", "token"), "ip")).IsValid.Should().BeFalse();
        enrollment.Validate(new EnrollOnboardingInstallCommand("challenge", new("android", ""), "ip")).IsValid.Should().BeFalse();
    }
}
