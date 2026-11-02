using FluentValidation;
using Orbit.Application.Auth.Commands;
using Orbit.Domain.Entities;

namespace Orbit.Application.Auth.Validators;

public sealed class OAuthClientRegistrationValidator : AbstractValidator<RegisterOAuthClientCommand>
{
    public OAuthClientRegistrationValidator()
    {
        RuleFor(value => value.ClientName).NotEmpty().MaximumLength(OAuthClient.MaxNameLength);
        RuleFor(value => value.RedirectUris).NotEmpty().Must(uris => uris is { Length: <= 50 });
        RuleForEach(value => value.RedirectUris).Must(value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0);
    }
}
