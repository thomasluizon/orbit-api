using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class TagTests
{
    private static readonly Guid ValidUserId = Guid.NewGuid();

    [Fact]
    public void Create_ValidInput_Success()
    {
        var result = Tag.Create(ValidUserId, "fitness", "#FF5733");

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be(ValidUserId);
        result.Value.Color.Should().Be("#FF5733");
    }

    [Fact]
    public void Create_EmptyUserId_Failure()
    {
        var result = Tag.Create(Guid.Empty, "fitness", "#FF5733");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("User ID is required");
    }

    [Fact]
    public void Create_EmptyName_Failure()
    {
        var result = Tag.Create(ValidUserId, "", "#FF5733");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Tag name is required");
    }

    [Fact]
    public void Create_NameOver50Chars_Failure()
    {
        var longName = new string('a', 51);

        var result = Tag.Create(ValidUserId, longName, "#FF5733");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("50 characters or less");
    }

    [Fact]
    public void Create_EmptyColor_Failure()
    {
        var result = Tag.Create(ValidUserId, "fitness", "");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Tag color is required");
    }

    [Fact]
    public void Create_CapitalizesName()
    {
        var result = Tag.Create(ValidUserId, "running", "#FF5733");

        result.Value.Name.Should().Be("Running");
    }

    [Fact]
    public void Update_ValidInput_Success()
    {
        var tag = Tag.Create(ValidUserId, "fitness", "#FF5733").Value;

        var result = tag.Update("health", "#00FF00");

        result.IsSuccess.Should().BeTrue();
        tag.Name.Should().Be("Health");
        tag.Color.Should().Be("#00FF00");
    }

    [Fact]
    public void Update_EmptyName_Failure()
    {
        var tag = Tag.Create(ValidUserId, "fitness", "#FF5733").Value;

        var result = tag.Update("", "#00FF00");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Tag name is required");
    }

    [Fact]
    public void Update_NameOver50Chars_Failure()
    {
        var tag = Tag.Create(ValidUserId, "fitness", "#FF5733").Value;
        var longName = new string('a', 51);

        var result = tag.Update(longName, "#00FF00");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("50 characters or less");
    }
}
