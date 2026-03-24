namespace Orbit.Application.Auth.Queries;

public record LoginResponse(Guid UserId, string Token, string Name, string Email, bool WasReactivated = false);
