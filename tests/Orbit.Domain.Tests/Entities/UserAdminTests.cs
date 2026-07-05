using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class UserAdminTests
{
    private static User CreateUser() => User.Create("Thomas", "thomas@example.com").Value;

    [Fact]
    public void NewUser_IsNotAdmin()
    {
        CreateUser().IsAdmin.Should().BeFalse();
    }

    [Fact]
    public void GrantAdmin_SetsIsAdmin()
    {
        var user = CreateUser();

        user.GrantAdmin();

        user.IsAdmin.Should().BeTrue();
    }

    [Fact]
    public void GrantAdmin_IsIdempotent()
    {
        var user = CreateUser();

        user.GrantAdmin();
        user.GrantAdmin();

        user.IsAdmin.Should().BeTrue();
    }
}
