using FluentAssertions;
using Orbit.Application.Common;
using Orbit.Infrastructure.Services.Notion;

namespace Orbit.Infrastructure.Tests.Services.Notion;

public class NotionTaskMapperTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 9, 15);

    [Fact]
    public void MapsRecordedTitleDateStatusAndIdentifiers()
    {
        var fetchedAtUtc = DateTime.UtcNow;
        var task = new NotionTaskMapper(NotionExamples.Source()).Map(NotionExamples.Element(NotionExamples.Page()),
            UserId, Today, TimeZoneInfo.Utc, fetchedAtUtc)!;
        task.Title.Should().Be("Launch checklist");
        task.DueDate.Should().Be(Today);
        task.Status.Should().Be("In progress");
        task.DatabaseId.Should().Be(NotionExamples.DatabaseId);
        task.PageId.Should().Be(Guid.Parse("be633bf1-dfa0-436d-b259-571129a590e5"));
        task.NotionLastEditedAtUtc.Should().Be(new DateTime(2023, 3, 8, 18, 25, 0, DateTimeKind.Utc));
        task.FetchedAtUtc.Should().Be(fetchedAtUtc);
    }

    [Theory]
    [InlineData("2026-09-13", false)]
    [InlineData("2026-09-14", true)]
    [InlineData("2026-09-29", true)]
    [InlineData("2026-09-30", false)]
    [InlineData(null, false)]
    public void CompletedTasksUseInclusiveUserDateWindow(string? due, bool retained)
    {
        var page = NotionExamples.Page(due);
        page["properties"]!["Status"]!["status"]!["id"] = "completed-option";
        var task = new NotionTaskMapper(NotionExamples.Source()).Map(NotionExamples.Element(page), UserId,
            Today, TimeZoneInfo.Utc, DateTime.UtcNow);
        (task is not null).Should().Be(retained);
    }

    [Fact]
    public void OpenStatusUsesGroupMembershipRegardlessOfCustomLabel()
    {
        var page = NotionExamples.Page("2020-01-01");
        page["properties"]!["Status"]!["status"]!["name"] = "Waiting for review";
        var task = new NotionTaskMapper(NotionExamples.Source()).Map(NotionExamples.Element(page), UserId,
            Today, TimeZoneInfo.Utc, DateTime.UtcNow);
        task.Should().NotBeNull();
        task!.Status.Should().Be("Waiting for review");
    }

    [Fact]
    public void SchemaWithoutDateOrStatusGivesTitlesOnly()
    {
        var task = new NotionTaskMapper(NotionExamples.Source(date: false, status: false))
            .Map(NotionExamples.Element(NotionExamples.Page()), UserId, Today, TimeZoneInfo.Utc, DateTime.UtcNow)!;
        task.Title.Should().Be("Launch checklist");
        task.DueDate.Should().BeNull();
        task.Status.Should().BeNull();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void CheckboxFalseIsOpen(bool done, bool retained)
    {
        var page = NotionExamples.Page(due: null, status: false, checkbox: true);
        page["properties"]!["Done"]!["checkbox"] = done;
        var task = new NotionTaskMapper(NotionExamples.Source(status: false, checkbox: true))
            .Map(NotionExamples.Element(page), UserId, Today, TimeZoneInfo.Utc, DateTime.UtcNow);
        (task is not null).Should().Be(retained);
    }

    [Theory]
    [InlineData("2026-09-15T01:00:00Z", "2026-09-14")]
    [InlineData("2026-09-15", "2026-09-15")]
    public void TimestampDatesUsePersonTimeZoneAndDateOnlyValuesKeepTheirDate(string start, string expected)
    {
        var task = new NotionTaskMapper(NotionExamples.Source()).Map(NotionExamples.Element(NotionExamples.Page(start)),
            UserId, Today, TimeZoneHelper.FindTimeZone("America/Sao_Paulo"), DateTime.UtcNow)!;
        task.DueDate.Should().Be(DateOnly.Parse(expected));
    }

    [Fact]
    public void NamedTimeZoneDateIsConvertedToPersonTimeZone()
    {
        var page = NotionExamples.Page("2026-09-15T01:00:00");
        page["properties"]!["Due date"]!["date"]!["time_zone"] = "Asia/Tokyo";
        var task = new NotionTaskMapper(NotionExamples.Source()).Map(NotionExamples.Element(page), UserId,
            Today, TimeZoneHelper.FindTimeZone("America/Sao_Paulo"), DateTime.UtcNow)!;
        task.DueDate.Should().Be(Today.AddDays(-1));
    }

    [Fact]
    public void CapsTitleAtTwoHundredCharacters()
    {
        var page = NotionExamples.Page();
        page["properties"]!["Name"]!["title"]![0]!["plain_text"] = new string('a', 300);
        var task = new NotionTaskMapper(NotionExamples.Source()).Map(NotionExamples.Element(page), UserId,
            Today, TimeZoneInfo.Utc, DateTime.UtcNow)!;
        task.Title.Should().HaveLength(200);
    }
}
