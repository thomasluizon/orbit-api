using FluentAssertions;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class AiFactExtractionBatchTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const string BatchId = "batch_abc123";
    private const string InputFileId = "file_input_456";

    [Fact]
    public void Create_ValidData_SetsSubmittedStatusAndIdsAndTimestamp()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var batch = AiFactExtractionBatch.Create(UserId, BatchId, InputFileId);

        var after = DateTime.UtcNow.AddSeconds(1);

        batch.Id.Should().NotBe(Guid.Empty);
        batch.UserId.Should().Be(UserId);
        batch.BatchId.Should().Be(BatchId);
        batch.InputFileId.Should().Be(InputFileId);
        batch.OutputFileId.Should().BeNull();
        batch.Status.Should().Be(AiFactExtractionBatchStatus.Submitted);
        batch.CreatedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        batch.CompletedAtUtc.Should().BeNull();
    }

    [Fact]
    public void MarkCompleted_SetsStatusOutputFileAndTimestamp()
    {
        var batch = AiFactExtractionBatch.Create(UserId, BatchId, InputFileId);
        var before = DateTime.UtcNow.AddSeconds(-1);

        batch.MarkCompleted("file_output_789");

        var after = DateTime.UtcNow.AddSeconds(1);

        batch.Status.Should().Be(AiFactExtractionBatchStatus.Completed);
        batch.OutputFileId.Should().Be("file_output_789");
        batch.CompletedAtUtc.Should().NotBeNull();
        batch.CompletedAtUtc!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void MarkFailed_SetsStatusAndTimestampWithoutOutputFile()
    {
        var batch = AiFactExtractionBatch.Create(UserId, BatchId, InputFileId);
        var before = DateTime.UtcNow.AddSeconds(-1);

        batch.MarkFailed();

        var after = DateTime.UtcNow.AddSeconds(1);

        batch.Status.Should().Be(AiFactExtractionBatchStatus.Failed);
        batch.OutputFileId.Should().BeNull();
        batch.CompletedAtUtc.Should().NotBeNull();
        batch.CompletedAtUtc!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
