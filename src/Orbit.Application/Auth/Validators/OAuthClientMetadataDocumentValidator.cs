using FluentValidation;
using Orbit.Application.Auth.Models;
using Orbit.Domain.Models;

namespace Orbit.Application.Auth.Validators;

public sealed class OAuthClientMetadataDocumentValidator : AbstractValidator<OAuthClientMetadataDocument>
{
    public OAuthClientMetadataDocumentValidator()
    {
        RuleFor(document => document.ClientId).Must(OAuthClientMetadata.IsMetadataUrl);
        RuleFor(document => document.ClientName).NotEmpty().MaximumLength(OAuthClientMetadata.MaxClientNameLength);
        RuleFor(document => document.RedirectUris).NotNull().Must(uris => uris is { Length: > 0 and <= 50 });
        RuleFor(document => document).Must(document => OAuthClientMetadata.Create(document.ClientId,
            document.ClientName, document.RedirectUris, document.TokenEndpointAuthMethod).IsSuccess);
    }
}
