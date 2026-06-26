using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface ITagSuggestionService
{
    Task<Result<IReadOnlyList<string>>> SuggestTagsAsync(
        string title,
        string? description,
        IReadOnlyList<string> existingTagNames,
        string language,
        CancellationToken cancellationToken = default);
}
