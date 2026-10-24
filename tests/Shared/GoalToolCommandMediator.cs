using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Orbit.Application.Goals.Commands;
using Orbit.Application.Goals.Services;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Tests.Shared;

public static class GoalToolCommandMediator
{
    public static IMediator Create(IGenericRepository<Goal> goals, IUnitOfWork work,
        IGenericRepository<Habit>? habits = null, IGenericRepository<GoalProgressLog>? logs = null,
        IGoalCompletionService? completion = null, bool configureTransactions = true)
    {
        if (configureTransactions)
        {
            work.ExecuteInSavepointAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result>>>(0)(call.ArgAt<CancellationToken>(1)));
            work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Goal>>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<Goal>>>>(0)(call.ArgAt<CancellationToken>(1)));
            work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result>>>(0)(call.ArgAt<CancellationToken>(1)));
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddMediatR(options => options.RegisterServicesFromAssembly(typeof(CreateGoalCommand).Assembly));
        services.AddValidatorsFromAssembly(typeof(CreateGoalCommand).Assembly);
        services.AddSingleton(CommandRepository.TrackAdds(goals)).AddSingleton(work);
        services.AddSingleton(CommandRepository.TrackAdds(habits ?? Substitute.For<IGenericRepository<Habit>>()));
        services.AddSingleton(logs ?? Substitute.For<IGenericRepository<GoalProgressLog>>());
        if (completion is null)
        {
            completion = Substitute.For<IGoalCompletionService>();
            completion.SyncDerivedGoalsAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                .Returns(async call => { await work.SaveChangesAsync(call.Arg<CancellationToken>()); return (IReadOnlyList<GoalCompletionUpdate>)Array.Empty<GoalCompletionUpdate>(); });
        }
        services.AddSingleton(completion);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        services.AddSingleton(dates);
        services.AddSingleton(Substitute.For<IGamificationService>());
        services.AddTransient<GoalRepositories>();
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }
}
