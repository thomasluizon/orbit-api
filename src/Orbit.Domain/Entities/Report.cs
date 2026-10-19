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

#pragma warning disable CS0649, S3459 // EF materializes retained report _reviewedAtUtc through the configured ReviewedAtUtc backing field; the persisted review stamp has no C# assignment. The retained Report.ReviewedAtUtc property reads _reviewedAtUtc from EF materialization, so the private field cannot be removed as unassigned. https://github.com/thomasluizon/orbit-tickets/issues/230
    private DateTime? _reviewedAtUtc;
#pragma warning restore CS0649, S3459

    /// <summary>
    /// UTC instant an admin reviewed this report; null until reviewed. Exposed as a read-only
    /// property over an explicitly-mapped backing field (see ConfigureReportEntity) so the column
    /// stays mapped without a private setter -- read-only auto-properties get dropped by EF
    /// convention. https://github.com/thomasluizon/orbit-api/pull/390
    /// </summary>
    public DateTime? ReviewedAtUtc => _reviewedAtUtc;

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
