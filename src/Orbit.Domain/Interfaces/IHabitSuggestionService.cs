using Orbit.Domain.Common;
using Orbit.Domain.Models;

namespace Orbit.Domain.Interfaces;

public interface IHabitSuggestionService
{
    /// <summary>
    /// Asks the AI for a setup suggestion (emoji, schedule, sub-habit breakdown) for a habit with
    /// the given title, written in the given language. Returns a sanitized suggestion on success, or
    /// a failure when the AI produced no usable output or was unavailable.
    /// </summary>
    Task<Result<HabitSetupSuggestion>> SuggestSetupAsync(string title, string language, CancellationToken ct = default);
}
