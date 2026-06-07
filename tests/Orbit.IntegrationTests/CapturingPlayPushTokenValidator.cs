using Orbit.Application.Common;

namespace Orbit.IntegrationTests;

/// <summary>
/// Test double for <see cref="IPlayPushTokenValidator"/> so RTDN integration tests can drive the
/// webhook auth boundary without a live Google-signed OIDC token. Shared across the Sequential
/// collection; tests set <see cref="IsValid"/> before their request.
/// </summary>
public sealed class CapturingPlayPushTokenValidator : IPlayPushTokenValidator
{
    public bool IsValid { get; set; } = true;

    public Task<bool> IsValidAsync(string authorizationHeader) => Task.FromResult(IsValid);
}
