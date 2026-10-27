using System.Linq.Expressions;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Common;
using Orbit.Application.Profile.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Application.Tests.Chat.Tools;

public sealed class SetProactiveAstraToolTests
{
    [Fact]
    public void Schema_RequiresBooleanEnabled()
    {
        var tool = new SetProactiveAstraTool(Substitute.For<IMediator>());
        var schema = JsonSerializer.SerializeToElement(tool.GetParameterSchema());

        schema.GetProperty("properties").GetProperty("enabled").GetProperty("type")
            .GetString().Should().Be("boolean");
        schema.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("enabled");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_ProUser_DispatchesCommandAndSavesSetting(bool enabled)
    {
        var user = User.Create("Person", "person@example.com").Value;
        user.GrantLifetimePro();
        user.SetProactiveAstraEnabled(!enabled);
        var repository = UserRepository(user);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        using var services = Services(repository, unitOfWork);
        var tool = new SetProactiveAstraTool(services.GetRequiredService<IMediator>());
        using var cancellation = new CancellationTokenSource();

        var result = await tool.ExecuteAsync(Arguments(enabled), user.Id, cancellation.Token);

        result.Success.Should().BeTrue();
        user.ProactiveAstraEnabled.Should().Be(enabled);
        result.EntityId.Should().Be(user.Id.ToString());
        JsonSerializer.SerializeToElement(result.Payload).GetProperty("enabled").GetBoolean()
            .Should().Be(enabled);
        await unitOfWork.Received(1).SaveChangesAsync(cancellation.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_FreeUser_ReturnsSamePayGateFailureAsCommand(bool enabled)
    {
        var user = User.Create("Person", "person@example.com").Value;
        user.StartTrial(DateTime.UtcNow.AddDays(-1));
        user.HasProAccess.Should().BeFalse();
        user.SetProactiveAstraEnabled(!enabled);
        var repository = UserRepository(user);
        var unitOfWork = Substitute.For<IUnitOfWork>();
        using var services = Services(repository, unitOfWork);
        var mediator = services.GetRequiredService<IMediator>();
        var expected = await mediator.Send(new SetProactiveAstraEnabledCommand(user.Id, enabled));
        var tool = new SetProactiveAstraTool(mediator);

        var result = await tool.ExecuteAsync(Arguments(enabled), user.Id, CancellationToken.None);

        expected.ErrorCode.Should().Be(Result.PayGateErrorCode);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(expected.ErrorCode);
        result.Error.Should().Be(expected.Error);
        user.ProactiveAstraEnabled.Should().Be(!enabled);
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await repository.DidNotReceive().FindOneTrackedAsync(
            Arg.Any<Expression<Func<User, bool>>>(),
            Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enabled\":null}")]
    [InlineData("{\"enabled\":\"false\"}")]
    [InlineData("{\"enabled\":0}")]
    public async Task InvalidEnabled_IsRejectedWithoutDispatch(string json)
    {
        var mediator = Substitute.For<IMediator>();
        var tool = new SetProactiveAstraTool(mediator);
        var arguments = JsonSerializer.Deserialize<JsonElement>(json);

        var result = await tool.ExecuteAsync(arguments, Guid.NewGuid(), CancellationToken.None);
        var check = await tool.CheckArgumentsAsync(arguments, Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse();
        check.IsFailure.Should().BeTrue();
        mediator.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckArguments_ValidBoolean_DoesNotMutate(bool enabled)
    {
        var user = User.Create("Person", "person@example.com").Value;
        var unitOfWork = Substitute.For<IUnitOfWork>();
        using var services = Services(UserRepository(user), unitOfWork);
        var tool = new SetProactiveAstraTool(services.GetRequiredService<IMediator>());

        var result = await tool.CheckArgumentsAsync(Arguments(enabled), user.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        user.ProactiveAstraEnabled.Should().BeTrue();
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_UnknownUser_PropagatesCommandFailure()
    {
        var repository = Substitute.For<IGenericRepository<User>>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        using var services = Services(repository, unitOfWork);
        var tool = new SetProactiveAstraTool(services.GetRequiredService<IMediator>());

        var result = await tool.ExecuteAsync(Arguments(false), Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Be(ErrorMessages.UserNotFound.Message);
        await unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static JsonElement Arguments(bool enabled) =>
        JsonSerializer.SerializeToElement(new { enabled });

    private static IGenericRepository<User> UserRepository(User user)
    {
        var repository = Substitute.For<IGenericRepository<User>>();
        repository.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        repository.FindOneTrackedAsync(Arg.Any<Expression<Func<User, bool>>>(),
                Arg.Any<Func<IQueryable<User>, IQueryable<User>>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Expression<Func<User, bool>>>(0).Compile()(user) ? user : null);
        return repository;
    }

    private static ServiceProvider Services(IGenericRepository<User> repository, IUnitOfWork unitOfWork)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssemblyContaining<SetProactiveAstraEnabledCommand>());
        services.AddSingleton(repository);
        services.AddSingleton(unitOfWork);
        services.AddSingleton<IPayGateService>(new PayGateService(
            Substitute.For<IGenericRepository<Habit>>(), repository,
            Substitute.For<IAppConfigService>(), Substitute.For<IUserDateService>(), Substitute.For<IFeatureFlagService>()));
        return services.BuildServiceProvider();
    }
}
