using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orbit.Application.Common;
using Orbit.Application.Marketing.Jobs;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Marketing.Commands;

public record SendMarketingBroadcastCommand(
    string SubjectEn, string SubjectPt, string BodyHtmlEn, string BodyHtmlPt, string? TestEmail)
    : IRequest<Result<MarketingBroadcastResult>>;

public record MarketingBroadcastResult(int RecipientCount, bool WasTest);

public partial class SendMarketingBroadcastCommandHandler(
    IGenericRepository<User> userRepository,
    IGenericRepository<MarketingContact> contactRepository,
    IBackgroundJobClient backgroundJobClient,
    IMarketingUnsubscribeTokenService unsubscribeTokenService,
    IOptions<MarketingSettings> marketingSettings,
    ILogger<SendMarketingBroadcastCommandHandler> logger,
    TimeProvider clock)
    : IRequestHandler<SendMarketingBroadcastCommand, Result<MarketingBroadcastResult>>
{
    private readonly MarketingSettings _settings = marketingSettings.Value;

    public async Task<Result<MarketingBroadcastResult>> Handle(SendMarketingBroadcastCommand request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.TestEmail))
        {
            var url = $"{_settings.ApiBaseUrl.TrimEnd('/')}/api/marketing/unsubscribe?token={Uri.EscapeDataString(unsubscribeTokenService.CreateToken(Guid.Empty))}&lang=en";
            backgroundJobClient.Enqueue<SendMarketingEmailJob>(job => job.ExecuteAsync(request.TestEmail,
                request.SubjectEn, request.BodyHtmlEn, "en", url));
            LogPreviewSent(logger, request.SubjectEn);
            return Result.Success(new MarketingBroadcastResult(1, true));
        }

        var pageSize = Math.Clamp(_settings.BroadcastPageSize, 1, 500);
        var broadcastId = Guid.NewGuid();
        var expiresAtUtc = clock.GetUtcNow().UtcDateTime.AddDays(SendMarketingBroadcastPageJob.RetryWindowDays);
        var pending = new List<MarketingRecipient>(pageSize);
        var pageNumber = 0;
        var total = 0;
        void Flush()
        {
            if (pending.Count == 0) return;
            var page = pending.ToArray();
            var number = pageNumber++;
            backgroundJobClient.Enqueue<SendMarketingBroadcastPageJob>(job => job.ExecuteAsync(
                broadcastId, number, request, page, expiresAtUtc, CancellationToken.None));
            pending.Clear();
        }
        void Add(MarketingRecipient recipient)
        {
            pending.Add(recipient);
            total++;
            if (pending.Count == pageSize) Flush();
        }

        Guid? cursor = null;
        while (true)
        {
            var users = await userRepository.FindIgnoringFiltersAsync(
                user => !user.IsDeactivated && user.MarketingEmailConsent == true
                    && (!cursor.HasValue || user.Id.CompareTo(cursor.Value) > 0),
                query => query.OrderBy(user => user.Id).Take(pageSize), cancellationToken);
            if (users.Count == 0) break;
            cursor = users[^1].Id;
            var emails = users.Select(user => user.Email.Trim().ToLowerInvariant()).ToList();
            var blocked = (await contactRepository.FindAsync(contact => emails.Contains(contact.Email)
                && (contact.UnsubscribedAtUtc != null || contact.SuppressedAtUtc != null), cancellationToken))
                .Select(contact => contact.Email).ToHashSet(StringComparer.Ordinal);
            foreach (var user in users.Where(user => !blocked.Contains(user.Email.Trim().ToLowerInvariant())))
                Add(new MarketingRecipient(user.Id, user.Email, user.Language ?? "en"));
            if (users.Count < pageSize) break;
        }
        cursor = null;
        while (true)
        {
            var contacts = await contactRepository.FindAsync(contact => contact.Source == "waitlist"
                && contact.UnsubscribedAtUtc == null && contact.SuppressedAtUtc == null
                && (!cursor.HasValue || contact.Id.CompareTo(cursor.Value) > 0),
                query => query.OrderBy(contact => contact.Id).Take(pageSize), cancellationToken);
            if (contacts.Count == 0) break;
            cursor = contacts[^1].Id;
            var emails = contacts.Select(contact => contact.Email).ToList();
            var userEmails = (await userRepository.FindIgnoringFiltersAsync(user => emails.Contains(user.Email.ToLower()),
                cancellationToken: cancellationToken)).Select(user => user.Email.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            foreach (var contact in contacts.Where(contact => !userEmails.Contains(contact.Email)))
                Add(new MarketingRecipient(contact.Id, contact.Email, contact.Language));
            if (contacts.Count < pageSize) break;
        }
        Flush();
        LogBroadcastQueued(logger, total, request.SubjectEn);
        return Result.Success(new MarketingBroadcastResult(total, false));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Marketing broadcast preview sent; subject={Subject}")]
    private static partial void LogPreviewSent(ILogger logger, string subject);
    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Marketing broadcast queued: {RecipientCount} recipients, subject={Subject}")]
    private static partial void LogBroadcastQueued(ILogger logger, int recipientCount, string subject);
}
