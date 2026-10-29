using FluentAssertions;
using Orbit.Application.Common;

namespace Orbit.Application.Tests.Common;

public class EvalAccountAccessTests
{
    private static readonly Guid Account = Guid.Parse("3485ef4e-3d7b-4a6f-87fb-c7f8a32d35ea");

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" , invalid, , ", false)]
    [InlineData("3485ef4e-3d7b-4a6f-87fb-c7f8a32d35eb", false)]
    [InlineData("3485ef4e-3d7b-4a6f-87fb-c7f8a32d35ea-extra", false)]
    [InlineData("3485ef4e-3d7b-4a6f-87fb-c7f8a32d35ea", true)]
    [InlineData("invalid, 3485EF4E-3D7B-4A6F-87FB-C7F8A32D35EA, ,", true)]
    public void Contains_RequiresAnExactValidAccountId(string? accountIds, bool expected)
    {
        EvalAccountAccess.Contains(accountIds, Account).Should().Be(expected);
    }

    [Fact]
    public void Contains_RejectsTheEmptyUserIdEvenWhenListed()
    {
        EvalAccountAccess.Contains(Guid.Empty.ToString(), Guid.Empty).Should().BeFalse();
    }
}
