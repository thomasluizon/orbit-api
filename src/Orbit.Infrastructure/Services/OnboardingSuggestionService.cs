using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;

namespace Orbit.Infrastructure.Services;

public sealed class OnboardingSuggestionService(AiCompletionClient client) : IOnboardingSuggestionService
{
    public Task<HabitSetupSuggestion?> SuggestAsync(OnboardingInput input, CancellationToken cancellationToken)
        => client.CompleteOnboardingSetupAsync(input.Title, input.Language, cancellationToken);
}
