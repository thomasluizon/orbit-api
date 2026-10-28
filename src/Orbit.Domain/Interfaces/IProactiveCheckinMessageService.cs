using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IProactiveCheckinMessageService
{
    Task<ProactiveMessage> GenerateAsync(ProactiveFacts facts, string language,
        IReadOnlyCollection<string> accountTitles, CancellationToken cancellationToken);
}
