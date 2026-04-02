namespace Orbit.Domain.Interfaces;

public interface IGeoLocationService
{
    Task<string> GetCountryCodeAsync(string? ipAddress, CancellationToken ct = default);
}
