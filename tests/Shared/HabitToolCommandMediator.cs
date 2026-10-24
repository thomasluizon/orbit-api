using FluentValidation;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Common;
using Orbit.Application.Goals.Services;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Tags.Commands;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;

namespace Orbit.Tests.Shared;

public static class HabitToolCommandMediator
{
    public static IMediator Create(IGenericRepository<Habit> habits, IGenericRepository<Tag> tags,
        IGenericRepository<Goal> goals, IUserDateService dates, IPayGateService quota, IUnitOfWork work,
        bool configureTransactions = true)
    {
        if (configureTransactions)
        {
            work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Orbit.Domain.Models.AgentExecuteOperationResponse>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<Func<CancellationToken, Task<Orbit.Domain.Models.AgentExecuteOperationResponse>>>(0)(call.ArgAt<CancellationToken>(1)));
            work.ExecuteInSavepointAsync(Arg.Any<Func<CancellationToken, Task<Orbit.Domain.Models.AgentExecuteOperationResponse>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<Func<CancellationToken, Task<Orbit.Domain.Models.AgentExecuteOperationResponse>>>(0)(call.ArgAt<CancellationToken>(1)));
            work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<Guid>>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<Guid>>>>(0)(call.ArgAt<CancellationToken>(1)));
            work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task<Result<BulkCreateResult>>>>(), Arg.Any<CancellationToken>())
                .Returns(call => call.ArgAt<Func<CancellationToken, Task<Result<BulkCreateResult>>>>(0)(call.ArgAt<CancellationToken>(1)));
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddMediatR(options => options.RegisterServicesFromAssembly(typeof(CreateHabitCommand).Assembly));
        services.AddValidatorsFromAssembly(typeof(CreateHabitCommand).Assembly);
        services.AddSingleton(CommandRepository.TrackAdds(habits)).AddSingleton(CommandRepository.TrackAdds(tags)).AddSingleton(CommandRepository.TrackAdds(goals)).AddSingleton(dates).AddSingleton(quota).AddSingleton(work);
        var config = Substitute.For<IAppConfigService>();
        config.GetAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<int>(1));
        services.AddSingleton(config);
        services.AddSingleton(Substitute.For<IGamificationService>());
        services.AddSingleton(Substitute.For<IGoalCompletionService>());
        services.AddSingleton(Substitute.For<IGenericRepository<GoogleCalendarSyncSuggestion>>());
        services.AddTransient<CreateHabitRepositories>();
        services.AddTransient<BulkCreateHabitsRepositories>();
        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }
}
