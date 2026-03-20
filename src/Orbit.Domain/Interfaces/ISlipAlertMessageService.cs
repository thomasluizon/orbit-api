using Orbit.Domain.Common;

namespace Orbit.Domain.Interfaces;

public interface ISlipAlertMessageService
{
    Task<Result<(string Title, string Body)>> GenerateMessageAsync(
        string habitTitle,
        DayOfWeek dayOfWeek,
        int? peakHour,
        string language,
        CancellationToken cancellationToken = default);
}
