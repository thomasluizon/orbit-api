using FluentAssertions;
using Microsoft.Extensions.Options;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Models;
using Orbit.Application.Auth.Validators;
using Orbit.Application.Common;
using Orbit.Domain.Entities;

namespace Orbit.Application.Tests.Validators;

public class DesktopHandoffValidatorTests
{
    private const string Redirect = "org.useorbit.desktop://auth/callback";
    private const string StagingRedirect = "org.useorbit.desktop.staging://auth/callback";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void DesktopValidators_UseOneCatalogCodeForEveryInvalidBinding()
    {
        var create = new CreateDesktopHandoffCommandValidator(Options.Create(new DesktopAuthOptions()))
            .Validate(new CreateDesktopHandoffCommand(Guid.NewGuid(), "", Challenge, "S256", Redirect));
        var exchange = new ExchangeDesktopHandoffCommandValidator()
            .Validate(new ExchangeDesktopHandoffCommand("", "state", new string('a', 43), Redirect));

        foreach (var result in new[] { create, exchange })
            result.Errors.Should().ContainSingle().Which.ErrorCode.Should().Be("VALIDATION_DESKTOP_HANDOFF_INVALID");
        ErrorCopy.TryResolve("VALIDATION_DESKTOP_HANDOFF_INVALID", false, [], out var english).Should().BeTrue();
        english.Should().Be(DesktopHandoff.InvalidHandoff);
        ErrorCopy.TryResolve("VALIDATION_DESKTOP_HANDOFF_INVALID", true, [], out var portuguese).Should().BeTrue();
        portuguese.Should().NotBeNullOrWhiteSpace().And.NotBe(english);
    }

    [Theory]
    [InlineData(Redirect, Redirect, true)]
    [InlineData(StagingRedirect, StagingRedirect, true)]
    [InlineData(Redirect, StagingRedirect, false)]
    [InlineData(StagingRedirect, Redirect, false)]
    [InlineData(Redirect, "https://attacker.example", false)]
    [InlineData(Redirect, Redirect + "?extra=value", false)]
    [InlineData(Redirect, "org.useorbit.desktop://auth/Callback", false)]
    public void Create_OnlyAcceptsExactEnvironmentRedirect(string allowed, string submitted, bool valid)
    {
        var validator = new CreateDesktopHandoffCommandValidator(Options.Create(new DesktopAuthOptions { RedirectUri = allowed }));
        var result = validator.Validate(new CreateDesktopHandoffCommand(Guid.NewGuid(), "state", Challenge, "S256", submitted));

        result.IsValid.Should().Be(valid);
        result.Errors.Should().OnlyContain(error => error.ErrorMessage == DesktopHandoff.InvalidHandoff);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("state")]
    [InlineData("state-length")]
    [InlineData("challenge")]
    [InlineData("method")]
    public void Create_RejectsMalformedBindings(string invalid)
    {
        var validator = new CreateDesktopHandoffCommandValidator(Options.Create(new DesktopAuthOptions()));
        var result = validator.Validate(new CreateDesktopHandoffCommand(
            invalid == "user" ? Guid.Empty : Guid.NewGuid(),
            invalid == "state" ? null! : invalid == "state-length" ? new string('s', 513) : "state",
            invalid == "challenge" ? new string('+', 43) : Challenge,
            invalid == "method" ? "plain" : "S256", Redirect));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(DesktopHandoff.InvalidHandoff);
    }

    [Theory]
    [InlineData(43, true)]
    [InlineData(128, true)]
    [InlineData(42, false)]
    [InlineData(129, false)]
    public void Exchange_EnforcesVerifierLength(int length, bool valid)
    {
        var validator = new ExchangeDesktopHandoffCommandValidator();
        validator.Validate(new ExchangeDesktopHandoffCommand(new string('A', 64), "state", new string('a', length), Redirect)).IsValid.Should().Be(valid);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("state")]
    [InlineData("state-length")]
    [InlineData("verifier")]
    [InlineData("redirect")]
    public void Exchange_MalformedInput_UsesOneGenericError(string invalid)
    {
        var validator = new ExchangeDesktopHandoffCommandValidator();
        var result = validator.Validate(new ExchangeDesktopHandoffCommand(invalid == "code" ? "" : new string('A', 64),
            invalid == "state" ? "" : invalid == "state-length" ? new string('s', 513) : "state",
            invalid == "verifier" ? new string('+', 43) : new string('a', 43), invalid == "redirect" ? "" : Redirect));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(DesktopHandoff.InvalidHandoff);
    }
}
