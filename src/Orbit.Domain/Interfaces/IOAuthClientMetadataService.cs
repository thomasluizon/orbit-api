using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IOAuthClientMetadataService
{
    Task<OAuthClientMetadata?> GetAsync(string clientId, CancellationToken cancellationToken);
}
