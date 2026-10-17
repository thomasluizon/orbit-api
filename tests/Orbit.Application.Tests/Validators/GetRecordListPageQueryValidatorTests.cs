using FluentAssertions;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Queries;
using Orbit.Application.Chat.Validators;

namespace Orbit.Application.Tests.Validators;

public class GetRecordListPageQueryValidatorTests
{
    private readonly GetRecordListPageQueryValidator _validator = new();

    [Theory]
    [InlineData("notifications")]
    [InlineData("tags")]
    [InlineData("templates")]
    [InlineData("keys")]
    public void ValidKinds_AcceptAccountBoundCursor(string kind)
    {
        var userId = Guid.NewGuid();

        _validator.Validate(new GetRecordListPageQuery(userId, kind,
            RecordListCursor.Create(userId, kind, 10))).IsValid.Should().BeTrue();
    }

    [Fact]
    public void InvalidAccountKindAndCursor_AreRejected()
    {
        var result = _validator.Validate(new GetRecordListPageQuery(Guid.Empty, "habits", new string('x', 257)));

        result.Errors.Select(error => error.PropertyName).Should().BeEquivalentTo(["UserId", "Kind", "Cursor"]);
        _validator.Validate(new GetRecordListPageQuery(Guid.NewGuid(), "tags", ""))
            .Errors.Should().Contain(error => error.PropertyName == "Cursor");
    }
}
