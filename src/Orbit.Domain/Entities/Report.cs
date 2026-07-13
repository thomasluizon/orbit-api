using Orbit.Domain.Common;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Entities;

public class Report : Entity
{
    public Guid ReporterId { get; private set; }
    public Guid ReportedUserId { get; private set; }
    public ReportReason Reason { get; private set; }
    public string? Details { get; private set; }
    public Guid? CheerId { get; private set; }
    public ReportStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ReviewedAtUtc { get; }

    private Report() { }

    public static Result<Report> Create(
        Guid reporterId,
        Guid reportedUserId,
        ReportReason reason,
        string? details,
        Guid? cheerId)
    {
        if (reporterId == reportedUserId)
            return Result.Failure<Report>(DomainErrors.CannotReportSelf);

        var trimmedDetails = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        if (trimmedDetails is not null && trimmedDetails.Length > DomainConstants.MaxReportDetailsLength)
            return Result.Failure<Report>(DomainErrors.ReportDetailsTooLong.Format(DomainConstants.MaxReportDetailsLength));

        return Result.Success(new Report
        {
            ReporterId = reporterId,
            ReportedUserId = reportedUserId,
            Reason = reason,
            Details = trimmedDetails,
            CheerId = cheerId,
            Status = ReportStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow
        });
    }
}
