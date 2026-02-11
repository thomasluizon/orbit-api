using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IFactExtractionService
{
    Task<Result<ExtractedFacts>> ExtractFactsAsync(
        string userMessage,
        string? aiResponse,
        IReadOnlyList<UserFact> existingFacts,
        CancellationToken cancellationToken = default);
}
