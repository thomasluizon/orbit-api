using FluentValidation;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Common;

namespace Orbit.Application.Auth.Validators;

public sealed class AppleNativeSignInCommandValidator : AbstractValidator<AppleNativeSignInCommand>
{
    public AppleNativeSignInCommandValidator()
    {
        RuleFor(command => command.IdentityToken).NotEmpty().MaximumLength(16_384);
        RuleFor(command => command.AuthorizationCode).NotEmpty().MaximumLength(4096);
        RuleFor(command => command.RawNonce).NotEmpty().Length(16, 256);
        RuleFor(command => command.GivenName).MaximumLength(100).Must(name => name is null || !name.Any(char.IsControl));
        RuleFor(command => command.FamilyName).MaximumLength(100).Must(name => name is null || !name.Any(char.IsControl));
        RuleFor(command => command.Language).NotEmpty().Must(language => AppConstants.SupportedLanguages.Contains(language));
        RuleFor(command => command.ReferralCode).MaximumLength(50);
    }
}

public sealed class StartAppleSignInCommandValidator : AbstractValidator<StartAppleSignInCommand>
{
    public StartAppleSignInCommandValidator()
    {
        RuleFor(command => command.State).NotEmpty().Length(16, 256);
        RuleFor(command => command.Language).NotEmpty().Must(language => AppConstants.SupportedLanguages.Contains(language));
        RuleFor(command => command.ReferralCode).MaximumLength(50);
    }
}

public sealed class CompleteAppleCallbackCommandValidator : AbstractValidator<CompleteAppleCallbackCommand>
{
    public CompleteAppleCallbackCommandValidator()
    {
        RuleFor(command => command.State).NotEmpty().MaximumLength(256);
        RuleFor(command => command.Code).NotEmpty().MaximumLength(4096);
        RuleFor(command => command.User).MaximumLength(16_384);
    }
}

public sealed class TakeAppleHandoffCommandValidator : AbstractValidator<TakeAppleHandoffCommand>
{
    public TakeAppleHandoffCommandValidator()
    {
        RuleFor(command => command.Handoff).NotEmpty().MaximumLength(256);
        RuleFor(command => command.State).NotEmpty().MaximumLength(256);
    }
}
