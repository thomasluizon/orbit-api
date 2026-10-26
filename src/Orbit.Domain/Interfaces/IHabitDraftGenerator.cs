using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IHabitDraftGenerator
{
    Task<Result<IReadOnlyList<HabitSuggestionDraft>>> GenerateAsync(HabitSuggestionRequest request, CancellationToken ct);
}
