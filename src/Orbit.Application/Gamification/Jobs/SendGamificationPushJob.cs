using Orbit.Domain.Interfaces;

namespace Orbit.Application.Gamification.Jobs;

public class SendGamificationPushJob(IPushNotificationService pushes)
{
    public Task ExecuteAsync(Guid userId, string title, string body, string url) =>
        pushes.SendToUserAsync(userId, title, body, url, CancellationToken.None);
}
