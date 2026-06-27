using FluentAssertions;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserHandleTests
{
    private static User CreateUser() => User.Create("Thomas", "thomas@example.com").Value;

    [Theory]
    [InlineData("abc")]
    [InlineData("user_123")]
    [InlineData("ABC")]
    [InlineData("a_b")]
    public void SetHandle_ValidHandle_SetsHandle(string handle)
    {
        var user = CreateUser();

        var result = user.SetHandle(handle);

        result.IsSuccess.Should().BeTrue();
        user.Handle.Should().Be(handle);
    }

    [Fact]
    public void SetHandle_MaxLengthHandle_SetsHandle()
    {
        var user = CreateUser();
        var handle = new string('a', DomainConstants.HandleMaxLength);

        var result = user.SetHandle(handle);

        result.IsSuccess.Should().BeTrue();
        user.Handle.Should().Be(handle);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("a.b")]
    [InlineData("a@b")]
    [InlineData("café1")]
    [InlineData("")]
    [InlineData("   ")]
    public void SetHandle_InvalidHandle_ReturnsFailureAndLeavesHandleUnset(string handle)
    {
        var user = CreateUser();

        var result = user.SetHandle(handle);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.InvalidHandle.Code);
        user.Handle.Should().BeNull();
    }

    [Fact]
    public void SetHandle_TooLongHandle_ReturnsFailure()
    {
        var user = CreateUser();
        var handle = new string('a', DomainConstants.HandleMaxLength + 1);

        var result = user.SetHandle(handle);

        result.IsFailure.Should().BeTrue();
        result.ErrorCode.Should().Be(DomainErrors.InvalidHandle.Code);
        user.Handle.Should().BeNull();
    }

    [Fact]
    public void SetHandle_InvalidAfterValid_LeavesExistingHandleUnchanged()
    {
        var user = CreateUser();
        user.SetHandle("validhandle");

        var result = user.SetHandle("not valid");

        result.IsFailure.Should().BeTrue();
        user.Handle.Should().Be("validhandle");
    }

    [Fact]
    public void SeedDefaultHandle_ProducesPrefixedSeventeenCharHandle()
    {
        var user = CreateUser();

        user.SeedDefaultHandle();

        user.Handle.Should().NotBeNull();
        user.Handle!.Should().HaveLength(17);
        user.Handle.Should().StartWith("user_");
    }

    [Fact]
    public void SeedDefaultHandle_MatchesDeterministicFormula()
    {
        var user = CreateUser();

        user.SeedDefaultHandle();

        var expected = "user_" + user.Id.ToString("N")[..12];
        user.Handle.Should().Be(expected);
    }

    [Fact]
    public void SeedDefaultHandle_IsDeterministic()
    {
        var user = CreateUser();

        user.SeedDefaultHandle();
        var first = user.Handle;
        user.SeedDefaultHandle();
        var second = user.Handle;

        second.Should().Be(first);
    }

    [Fact]
    public void SeedDefaultHandle_ResultIsAValidSettableHandle()
    {
        var user = CreateUser();
        user.SeedDefaultHandle();
        var seeded = user.Handle!;

        var result = CreateUser().SetHandle(seeded);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void SetSocialOptIn_DefaultsToFalse()
    {
        var user = CreateUser();

        user.SocialOptIn.Should().BeFalse();
    }

    [Fact]
    public void SetSocialOptIn_TogglesValue()
    {
        var user = CreateUser();

        user.SetSocialOptIn(true);
        user.SocialOptIn.Should().BeTrue();

        user.SetSocialOptIn(false);
        user.SocialOptIn.Should().BeFalse();
    }
}
