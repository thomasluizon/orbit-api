using System.Text;
using FluentAssertions;
using Orbit.Application.Profile.Commands;
using Orbit.Application.Profile.Validators;
using Orbit.Application.Social.Commands;
using Orbit.Application.Social.Queries;
using Orbit.Application.Social.Validators;
using Orbit.Domain.Enums;

namespace Orbit.Application.Tests.Social;

public class SocialValidatorsTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    [Theory]
    [InlineData("handle", null, true)]
    [InlineData(null, "REF123", true)]
    [InlineData(null, null, false)]
    [InlineData("handle", "REF123", false)]
    public void SendFriendRequest_RequiresExactlyOneIdentifier(string? handle, string? referralCode, bool expectedValid)
    {
        var validator = new SendFriendRequestCommandValidator();
        var result = validator.Validate(new SendFriendRequestCommand(UserId, handle, referralCode));
        result.IsValid.Should().Be(expectedValid);
    }

    [Fact]
    public void SendCheer_RejectsNoteOver200Chars()
    {
        var validator = new SendCheerCommandValidator();
        var result = validator.Validate(new SendCheerCommand(UserId, Guid.NewGuid(), Guid.NewGuid(), new string('x', 201)));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SendCheer_RejectsCheeringSelf()
    {
        var validator = new SendCheerCommandValidator();
        var result = validator.Validate(new SendCheerCommand(UserId, UserId, Guid.NewGuid(), "hi"));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void SendCheer_AcceptsValidCommand()
    {
        var validator = new SendCheerCommandValidator();
        var result = validator.Validate(new SendCheerCommand(UserId, Guid.NewGuid(), Guid.NewGuid(), "nice work"));
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Report_RejectsUnknownReason()
    {
        var validator = new ReportUserCommandValidator();
        var result = validator.Validate(new ReportUserCommand(UserId, Guid.NewGuid(), (ReportReason)999, null, null));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Report_RejectsDetailsOver500Chars()
    {
        var validator = new ReportUserCommandValidator();
        var result = validator.Validate(new ReportUserCommand(UserId, Guid.NewGuid(), ReportReason.Spam, new string('x', 501), null));
        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("received", true)]
    [InlineData("sent", true)]
    [InlineData("everything", false)]
    public void GetCheers_ValidatesDirection(string direction, bool expectedValid)
    {
        var validator = new GetCheersQueryValidator();
        var result = validator.Validate(new GetCheersQuery(UserId, direction));
        result.IsValid.Should().Be(expectedValid);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(51, false)]
    [InlineData(30, true)]
    [InlineData(null, true)]
    public void GetFriendFeed_ValidatesPageSize(int? pageSize, bool expectedValid)
    {
        var validator = new GetFriendFeedQueryValidator();
        var result = validator.Validate(new GetFriendFeedQuery(UserId, null, pageSize));
        result.IsValid.Should().Be(expectedValid);
    }

    [Fact]
    public void GetFriendFeed_RejectsMalformedCursor()
    {
        var validator = new GetFriendFeedQueryValidator();
        var result = validator.Validate(new GetFriendFeedQuery(UserId, "!!!not-a-cursor!!!", null));
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void GetFriendFeed_AcceptsWellFormedCursor()
    {
        var raw = $"{DateTime.UtcNow.Ticks}:{Guid.NewGuid():N}";
        var cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var validator = new GetFriendFeedQueryValidator();
        var result = validator.Validate(new GetFriendFeedQuery(UserId, cursor, null));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("ab", false)]
    [InlineData("has space", false)]
    [InlineData("bad-dash", false)]
    [InlineData("good_handle", true)]
    public void SetHandle_ValidatesFormat(string handle, bool expectedValid)
    {
        var validator = new SetHandleCommandValidator();
        var result = validator.Validate(new SetHandleCommand(UserId, handle));
        result.IsValid.Should().Be(expectedValid);
    }
}
