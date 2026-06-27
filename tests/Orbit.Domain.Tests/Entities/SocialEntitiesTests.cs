using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;

namespace Orbit.Domain.Tests.Entities;

public class SocialEntitiesTests
{
    [Fact]
    public void Friendship_Create_DifferentUsers_ReturnsPendingRequest()
    {
        var requesterId = Guid.NewGuid();
        var addresseeId = Guid.NewGuid();

        var result = Friendship.Create(requesterId, addresseeId);

        result.IsSuccess.Should().BeTrue();
        result.Value.RequesterId.Should().Be(requesterId);
        result.Value.AddresseeId.Should().Be(addresseeId);
        result.Value.Status.Should().Be(FriendshipStatus.Pending);
        result.Value.RespondedAtUtc.Should().BeNull();
        result.Value.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Friendship_Create_SameUser_ReturnsCannotFriendSelf()
    {
        var userId = Guid.NewGuid();

        var result = Friendship.Create(userId, userId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.CannotFriendSelf.Code);
    }

    [Fact]
    public void Friendship_Accept_PendingRequest_TransitionsToAccepted()
    {
        var friendship = Friendship.Create(Guid.NewGuid(), Guid.NewGuid()).Value;

        var result = friendship.Accept();

        result.IsSuccess.Should().BeTrue();
        friendship.Status.Should().Be(FriendshipStatus.Accepted);
        friendship.RespondedAtUtc.Should().NotBeNull();
        friendship.RespondedAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Friendship_Accept_AlreadyAccepted_ReturnsFriendshipNotPending()
    {
        var friendship = Friendship.Create(Guid.NewGuid(), Guid.NewGuid()).Value;
        friendship.Accept();

        var result = friendship.Accept();

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.FriendshipNotPending.Code);
        friendship.Status.Should().Be(FriendshipStatus.Accepted);
    }

    [Fact]
    public void Cheer_Create_ValidInput_ReturnsSuccess()
    {
        var senderId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var habitId = Guid.NewGuid();

        var result = Cheer.Create(senderId, recipientId, habitId, "Great job");

        result.IsSuccess.Should().BeTrue();
        result.Value.SenderId.Should().Be(senderId);
        result.Value.RecipientId.Should().Be(recipientId);
        result.Value.HabitId.Should().Be(habitId);
        result.Value.Note.Should().Be("Great job");
        result.Value.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Cheer_Create_SenderEqualsRecipient_ReturnsCannotCheerSelf()
    {
        var userId = Guid.NewGuid();

        var result = Cheer.Create(userId, userId, Guid.NewGuid(), null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.CannotCheerSelf.Code);
    }

    [Fact]
    public void Cheer_Create_NoteExceedsMaxLength_ReturnsCheerNoteTooLong()
    {
        var note = new string('a', DomainConstants.MaxCheerNoteLength + 1);

        var result = Cheer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), note);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.CheerNoteTooLong.Code);
    }

    [Fact]
    public void Cheer_Create_NoteAtMaxLength_ReturnsSuccess()
    {
        var note = new string('a', DomainConstants.MaxCheerNoteLength);

        var result = Cheer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), note);

        result.IsSuccess.Should().BeTrue();
        result.Value.Note.Should().Be(note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Cheer_Create_BlankNote_StoresNull(string? note)
    {
        var result = Cheer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), note);

        result.IsSuccess.Should().BeTrue();
        result.Value.Note.Should().BeNull();
    }

    [Fact]
    public void Cheer_Create_NoteWithSurroundingWhitespace_IsTrimmed()
    {
        var result = Cheer.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "  Keep going  ");

        result.IsSuccess.Should().BeTrue();
        result.Value.Note.Should().Be("Keep going");
    }

    [Fact]
    public void BlockedUser_Create_DifferentUsers_ReturnsSuccess()
    {
        var blockerId = Guid.NewGuid();
        var blockedId = Guid.NewGuid();

        var result = BlockedUser.Create(blockerId, blockedId);

        result.IsSuccess.Should().BeTrue();
        result.Value.BlockerId.Should().Be(blockerId);
        result.Value.BlockedId.Should().Be(blockedId);
        result.Value.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void BlockedUser_Create_SameUser_ReturnsCannotBlockSelf()
    {
        var userId = Guid.NewGuid();

        var result = BlockedUser.Create(userId, userId);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.CannotBlockSelf.Code);
    }

    [Fact]
    public void Report_Create_ValidInput_ReturnsPending()
    {
        var reporterId = Guid.NewGuid();
        var reportedUserId = Guid.NewGuid();

        var result = Report.Create(reporterId, reportedUserId, ReportReason.Spam, "Sending spam links", null);

        result.IsSuccess.Should().BeTrue();
        result.Value.ReporterId.Should().Be(reporterId);
        result.Value.ReportedUserId.Should().Be(reportedUserId);
        result.Value.Reason.Should().Be(ReportReason.Spam);
        result.Value.Details.Should().Be("Sending spam links");
        result.Value.Status.Should().Be(ReportStatus.Pending);
        result.Value.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(ReportReason.Spam)]
    [InlineData(ReportReason.Harassment)]
    [InlineData(ReportReason.InappropriateContent)]
    [InlineData(ReportReason.Impersonation)]
    [InlineData(ReportReason.Other)]
    public void Report_Create_StoresReason(ReportReason reason)
    {
        var result = Report.Create(Guid.NewGuid(), Guid.NewGuid(), reason, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Reason.Should().Be(reason);
    }

    [Fact]
    public void Report_Create_SameUser_ReturnsCannotReportSelf()
    {
        var userId = Guid.NewGuid();

        var result = Report.Create(userId, userId, ReportReason.Spam, null, null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.CannotReportSelf.Code);
    }

    [Fact]
    public void Report_Create_DetailsExceedMaxLength_ReturnsReportDetailsTooLong()
    {
        var details = new string('a', DomainConstants.MaxReportDetailsLength + 1);

        var result = Report.Create(Guid.NewGuid(), Guid.NewGuid(), ReportReason.Other, details, null);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.ReportDetailsTooLong.Code);
    }

    [Fact]
    public void Report_Create_DetailsAtMaxLength_ReturnsSuccess()
    {
        var details = new string('a', DomainConstants.MaxReportDetailsLength);

        var result = Report.Create(Guid.NewGuid(), Guid.NewGuid(), ReportReason.Other, details, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Details.Should().Be(details);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Report_Create_BlankDetails_StoresNull(string? details)
    {
        var result = Report.Create(Guid.NewGuid(), Guid.NewGuid(), ReportReason.Spam, details, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Details.Should().BeNull();
    }

    [Fact]
    public void Report_Create_DetailsWithSurroundingWhitespace_IsTrimmed()
    {
        var result = Report.Create(Guid.NewGuid(), Guid.NewGuid(), ReportReason.Spam, "  abusive  ", null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Details.Should().Be("abusive");
    }

    [Fact]
    public void Report_Create_NullCheerId_StoresNull()
    {
        var result = Report.Create(Guid.NewGuid(), Guid.NewGuid(), ReportReason.Spam, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.CheerId.Should().BeNull();
    }

    [Fact]
    public void Report_Create_NonNullCheerId_StoresValue()
    {
        var cheerId = Guid.NewGuid();

        var result = Report.Create(Guid.NewGuid(), Guid.NewGuid(), ReportReason.InappropriateContent, null, cheerId);

        result.IsSuccess.Should().BeTrue();
        result.Value.CheerId.Should().Be(cheerId);
    }

    [Fact]
    public void FriendFeedEvent_StreakMilestone_SetsTypeAndValue()
    {
        var actorUserId = Guid.NewGuid();

        var feedEvent = FriendFeedEvent.StreakMilestone(actorUserId, 30);

        feedEvent.ActorUserId.Should().Be(actorUserId);
        feedEvent.Type.Should().Be(FriendFeedEventType.StreakMilestone);
        feedEvent.Value.Should().Be(30);
        feedEvent.AchievementId.Should().BeNull();
        feedEvent.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FriendFeedEvent_AchievementUnlocked_SetsTypeAndAchievementId()
    {
        var actorUserId = Guid.NewGuid();

        var feedEvent = FriendFeedEvent.AchievementUnlocked(actorUserId, "first_habit");

        feedEvent.ActorUserId.Should().Be(actorUserId);
        feedEvent.Type.Should().Be(FriendFeedEventType.AchievementUnlocked);
        feedEvent.AchievementId.Should().Be("first_habit");
        feedEvent.Value.Should().BeNull();
        feedEvent.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FriendFeedEvent_HabitCompletedMilestone_SetsAllFields()
    {
        var actorUserId = Guid.NewGuid();

        var feedEvent = FriendFeedEvent.HabitCompletedMilestone(actorUserId, "century_club", 100);

        feedEvent.ActorUserId.Should().Be(actorUserId);
        feedEvent.Type.Should().Be(FriendFeedEventType.HabitCompletedMilestone);
        feedEvent.Value.Should().Be(100);
        feedEvent.AchievementId.Should().Be("century_club");
        feedEvent.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }
}
