using Orbit.Infrastructure.Configuration;

namespace Orbit.Infrastructure.AI;

internal static class AiEvalCredentials
{
    internal static string Resolve(AiSettings settings, AiEvalAccount account)
    {
        if (!account.IsEvaluation)
            return settings.ApiKey;
        return !string.IsNullOrWhiteSpace(settings.EvalApiKey) ? settings.EvalApiKey
            : throw new InvalidOperationException("Evaluation AI credentials are required.");
    }
}
