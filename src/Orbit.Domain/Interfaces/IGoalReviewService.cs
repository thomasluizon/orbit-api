using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface IGoalReviewService
{
    Task<Result<string>> GenerateReviewAsync(
        string goalsContext,
        string language,
        CancellationToken cancellationToken = default);
}
