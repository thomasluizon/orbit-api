using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IFactExtractionService
{
    Task<Result<ExtractedFacts>> ExtractFactsAsync(
        string userMessage,
        string? aiResponse,
        CancellationToken cancellationToken = default);
}
