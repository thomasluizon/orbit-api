using System.Data.Common;
using System.Net;
using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Goals.Services;
using Orbit.Application.Common;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.AI;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;

namespace Orbit.Infrastructure.Tests.Persistence;

public class ChatModerationConcurrencyTests
{
    [Fact]
    public async Task Handle_PendingModerationFlagRead_LoadsRequestContextConcurrently()
    {
        var pending = new PendingFlagRead();
        using var factory = new SqliteOrbitDbContextFactory(pending);
        var db = factory.Context;
        var user = User.Create("Alex", "moderation-concurrency@example.test").Value;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "Test"));
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Staging");
        using var services = new ServiceCollection()
            .AddScoped(_ => factory.CreateContext())
            .AddScoped<IFeatureFlagService, FeatureFlagService>()
            .AddSingleton<IMemoryCache>(new MemoryCache(new MemoryCacheOptions()))
            .AddSingleton<IHostEnvironment>(environment)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddSingleton<IHttpContextAccessor>(http)
            .BuildServiceProvider();
        using var requestScope = services.CreateScope();
        var requestDb = requestScope.ServiceProvider.GetRequiredService<OrbitDbContext>();
        var flags = requestScope.ServiceProvider.GetRequiredService<IFeatureFlagService>();
        using var transport = new PendingModerationResponse();
        using var client = new HttpClient(transport);
        var moderation = ActivatorUtilities.CreateInstance<ContentModerationService>(requestScope.ServiceProvider,
            client, Options.Create(new AiSettings { ApiKey = "test-key" }), NullLogger<ContentModerationService>.Instance);
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(user.Id, Arg.Any<CancellationToken>()).Returns(new DateOnly(2026, 4, 3));
        var intent = Substitute.For<IAiIntentService>();
        intent.SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AiResponse>(ErrorMessages.AiUnavailable));
        var quota = Substitute.For<IPayGateService>();
        quota.TryConsumeAiMessage(user.Id, Arg.Any<IUnitOfWork>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        var work = Substitute.For<IUnitOfWork>();
        work.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        var handler = new ProcessUserChatCommandHandler(
            new(new GenericRepository<Habit>(requestDb), new GenericRepository<Goal>(requestDb), new GenericRepository<User>(requestDb),
                new GenericRepository<Tag>(requestDb), new GenericRepository<ChecklistTemplate>(requestDb), flags),
            new(intent, new AiToolRegistry([]), Substitute.For<ISystemPromptBuilder>(), Substitute.For<IAgentCatalogService>()),
            new(dates, Substitute.For<IUserStreakService>(), quota, work, services.GetRequiredService<IServiceScopeFactory>(),
                Substitute.For<IAgentOperationExecutor>(), Substitute.For<IPendingClarificationStore>(),
                Substitute.For<IGoalProgressReadSyncer>(), Substitute.For<IGamificationService>(), Substitute.For<IMediator>(),
                Substitute.For<IProductAnalytics>(), Substitute.For<IHabitScheduleLogReader>(),
                Substitute.For<IIssuedAstraBlockStore>(), Substitute.For<IPendingAgentOperationStore>()),
            NullLogger<ProcessUserChatCommandHandler>.Instance, Substitute.For<IAppConfigService>(), moderation);
        pending.Enabled = true;

        var turn = handler.Handle(new(user.Id, "Hello"), default);
        try
        {
            await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            pending.Release.TrySetResult();
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await pending.HabitsRead.Task.WaitAsync(TimeSpan.FromSeconds(10));
            intent.ReceivedCalls().Should().BeEmpty();
        }
        finally
        {
            pending.Release.TrySetResult();
            transport.Release.TrySetResult();
            await turn;
        }
        var result = await turn;

        pending.FlagContext.Should().NotBeSameAs(requestDb);
        result.ErrorCode.Should().Be(ErrorCodes.AiUnavailable);
        await intent.Received(1).SendWithToolsAsync(Arg.Any<AiToolRequest>(), Arg.Any<Func<AiStreamEvent, Task>?>(), Arg.Any<CancellationToken>());
    }

    private sealed class PendingFlagRead : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public DbContext? FlagContext { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HabitsRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && FlagContext is null && command.CommandText.Contains("FROM \"Users\"", StringComparison.Ordinal))
            {
                FlagContext = eventData.Context;
                Started.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            if (Enabled && command.CommandText.Contains("FROM \"Habits\"", StringComparison.Ordinal))
                HabitsRead.TrySetResult();
            return result;
        }
    }

    private sealed class PendingModerationResponse : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"results":[{"flagged":false,"categories":{}}]}""") };
        }
    }
}
