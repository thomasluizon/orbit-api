namespace Orbit.Domain.Interfaces;

public interface IAppConfigService
{
    Task<T> GetAsync<T>(string key, T defaultValue, CancellationToken cancellationToken = default);
    Task<Dictionary<string, string>> GetAllAsync(CancellationToken cancellationToken = default);
}
