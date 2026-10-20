using FluentAssertions;
using Orbit.Domain.Entities;

namespace Orbit.Domain.Tests.Entities;

public class NotionTests
{
    [Fact]
    public void Connection_PreservesCredentialsAndTransitionsBetweenPollStates()
    {
        var connection = NotionConnection.Create(Guid.NewGuid(), "access", "refresh", Guid.NewGuid(), "Workspace", Guid.NewGuid());
        connection.AccessToken.Should().Be("access");
        connection.RefreshToken.Should().Be("refresh");
        connection.Status.Should().Be(NotionConnectionStatus.Idle);
        connection.FailPoll(false, DateTime.UtcNow);
        connection.Status.Should().Be(NotionConnectionStatus.TransientError);
        connection.FailPoll(true, DateTime.UtcNow);
        connection.Status.Should().Be(NotionConnectionStatus.ReconnectRequired);
        connection.CompletePoll("[]", DateTime.UtcNow);
        connection.Status.Should().Be(NotionConnectionStatus.Idle);
        connection.LastPollAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Connection_RejectsMissingOwnerOrCredentials()
    {
        var create = () => NotionConnection.Create(Guid.Empty, "access", null, Guid.NewGuid(), null, Guid.NewGuid());
        create.Should().Throw<ArgumentException>();
        create = () => NotionConnection.Create(Guid.NewGuid(), " ", null, Guid.NewGuid(), null, Guid.NewGuid());
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Task_RejectsOversizedTitleAndUnsafeUrl()
    {
        var create = () => NotionTask.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 201),
            null, null, "https://notion.so/page", DateTime.UtcNow, DateTime.UtcNow);
        create.Should().Throw<ArgumentException>();
        create = () => NotionTask.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Task",
            null, null, "javascript:alert(1)", DateTime.UtcNow, DateTime.UtcNow);
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Task_AllowsUntitledPagesAndNullableProperties()
    {
        var task = NotionTask.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "",
            null, null, "https://notion.so/page", DateTime.UtcNow, DateTime.UtcNow);
        task.Title.Should().BeEmpty();
        task.DueDate.Should().BeNull();
        task.Status.Should().BeNull();
    }

    [Fact]
    public void State_ExpiresExactlyTenMinutesAfterIssue()
    {
        var nowUtc = DateTime.UtcNow;
        var state = NotionOAuthState.Create(Guid.NewGuid(), new string('a', 64), "https://useorbit.org/callback", nowUtc);
        state.ExpiresAtUtc.Should().Be(nowUtc.AddMinutes(10));
    }

    [Fact]
    public void State_RejectsInvalidHashAndRelativeRedirect()
    {
        var create = () => NotionOAuthState.Create(Guid.NewGuid(), "guess", "https://useorbit.org/callback", DateTime.UtcNow);
        create.Should().Throw<ArgumentException>();
        create = () => NotionOAuthState.Create(Guid.NewGuid(), new string('a', 64), "/callback", DateTime.UtcNow);
        create.Should().Throw<ArgumentException>();
    }
}
