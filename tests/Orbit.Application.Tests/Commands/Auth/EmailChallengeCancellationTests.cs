using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.ApiKeys.Commands;
using Orbit.Application.ApiKeys.Services;
using Orbit.Application.Auth.Commands;
using Orbit.Application.Auth.Services;
using Orbit.Application.Chat.Tools;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Commands.Auth;

public class EmailChallengeCancellationTests
{
    [Theory]
    [InlineData("request-key")]
    [InlineData("confirm-key")]
    [InlineData("request-deletion")]
    [InlineData("confirm-deletion")]
    [InlineData("preview-deletion")]
    public async Task CancellationAfterUserLookup_StopsChallengeWorkAndPreservesState(string path)
    {
        using var cancellation = new CancellationTokenSource();
        var database = new ChallengeTestDatabase();
        var service = new EmailChallengeService(database.Store());
        var user = User.Create("Cancellation", "cancel@example.com").Value;
        var users = Substitute.For<IGenericRepository<User>>();
        var jobs = Substitute.For<IBackgroundJobClient>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var sessions = Substitute.For<IAuthSessionService>();
        sessions.RevokeAllSessionsAsync(user.Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<DateTime>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task<Result<DateTime>>>>()(call.Arg<CancellationToken>()));
        var authorization = new ApiKeyManagementAuthorization(Substitute.For<IAppConfigService>(), service);
        var key = path.Contains("key", StringComparison.Ordinal) ? "api-key-create:cancel@example.com" : "delete:cancel@example.com";
        var confirming = !path.StartsWith("request", StringComparison.Ordinal);
        if (confirming)
            database.Seed(key, "123456");
        users.GetByIdAsync(user.Id, cancellation.Token).Returns(_ =>
        {
            cancellation.Cancel();
            return user;
        });
        using var services = new ServiceCollection()
            .AddSingleton(users).AddSingleton(service).BuildServiceProvider();

        Func<Task> act = path switch
        {
            "request-key" => async () => await new RequestApiKeyCreationChallengeCommandHandler(service, users, jobs)
                .Handle(new(user.Id), cancellation.Token),
            "confirm-key" => async () => await new ConfirmApiKeyCreationChallengeCommandHandler(service, authorization, users)
                .Handle(new(user.Id, "123456"), cancellation.Token),
            "request-deletion" => async () => await new RequestAccountDeletionCommandHandler(service, users, jobs)
                .Handle(new(user.Id), cancellation.Token),
            "confirm-deletion" => async () => await new ConfirmAccountDeletionCommandHandler(service, users, unitOfWork, jobs,
                    sessions, Substitute.For<IGenericRepository<ApiKey>>())
                .Handle(new(user.Id, "123456"), cancellation.Token),
            "preview-deletion" => async () => await new CheckChatCommandQueryHandler(services)
                .Handle(new(new ConfirmAccountDeletionCommand(user.Id, "999999")), cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(path))
        };

        await act.Should().ThrowAsync<OperationCanceledException>();

        var row = database.Row(key);
        if (confirming)
        {
            row.Should().NotBeNull();
            row!.Consumed.Should().BeFalse();
            row.Attempts.Should().Be(0);
        }
        else
            row.Should().BeNull();
        (await service.HasAuthorizationAsync(EmailChallengeOperation.ApiKeyManagement, user.Id)).Should().BeFalse();
        user.IsDeactivated.Should().BeFalse();
        jobs.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<IState>());
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
