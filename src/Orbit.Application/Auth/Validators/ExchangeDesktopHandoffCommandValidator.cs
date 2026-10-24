using FluentValidation;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Entities;

namespace Orbit.Application.Auth.Validators;

public sealed class ExchangeDesktopHandoffCommandValidator : AbstractValidator<ExchangeDesktopHandoffCommand>
{
    public ExchangeDesktopHandoffCommandValidator()
    {
        RuleFor(request => request).Must(request => DesktopHandoff.IsValidCode(request.Code)
            && DesktopHandoff.IsValidState(request.State)
            && DesktopHandoff.IsValidVerifier(request.CodeVerifier)
            && !string.IsNullOrWhiteSpace(request.RedirectUri) && request.RedirectUri.Length <= 2048)
            .WithMessage(DesktopHandoff.InvalidHandoff);
    }
}
