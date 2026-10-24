using FluentValidation;
using Microsoft.Extensions.Options;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Models;
using Orbit.Domain.Entities;

namespace Orbit.Application.Auth.Validators;

public sealed class CreateDesktopHandoffCommandValidator : AbstractValidator<CreateDesktopHandoffCommand>
{
    public CreateDesktopHandoffCommandValidator(IOptions<DesktopAuthOptions> options)
    {
        RuleFor(request => request).Must(request => request.UserId != Guid.Empty
            && DesktopHandoff.IsValidState(request.State)
            && DesktopHandoff.IsValidChallenge(request.CodeChallenge)
            && request.CodeChallengeMethod == "S256"
            && DesktopHandoff.IsAllowedRedirect(request.RedirectUri, options.Value.RedirectUri))
            .WithMessage(DesktopHandoff.InvalidHandoff);
    }
}
