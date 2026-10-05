using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat.Tools;
using Orbit.Application.Chat.Tools.Implementations;
using Orbit.Application.Habits.Commands;
using Orbit.Application.Habits.Validators;
using Orbit.Domain.Common;
using Orbit.Domain.Entities;
using Orbit.Domain.Enums;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Services;

public sealed class BulkCreateAgentOperationTests : IDisposable
{
    private readonly SqliteOrbitDbContextFactory _factory = new();
    private readonly IPayGateService _payGate = Substitute.For<IPayGateService>();
    private readonly IAgentAuditService _audit = Substitute.For<IAgentAuditService>();
    private readonly PendingAgentOperationStore _pending;
    private readonly AgentOperationExecutor _executor;
    private readonly ServiceProvider _provider;
    private readonly Guid _userId;
    private AgentAuditEntry? _lastAudit;

    public BulkCreateAgentOperationTests()
    {
        var context = _factory.Context;
        var user = User.Create("Alex", "alex@example.com").Value;
        _userId = user.Id;
        context.Users.Add(user);
        context.SaveChanges();

        var settings = Options.Create(new AgentPlatformSettings());
        _pending = new PendingAgentOperationStore(context, settings);
        var bridge = Substitute.For<IAgentStepUpAuthorizationBridge>();
        bridge.GetRequiredConfirmationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentConfirmationRequirement?)null);
        var work = new UnitOfWork(context, new DatabaseConnectionSettings());
        _payGate.CanCreateHabits(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _payGate.CanCreateSubHabits(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        var dates = Substitute.For<IUserDateService>();
        dates.GetUserTodayAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new DateOnly(2026, 3, 20));

        _provider = new ServiceCollection()
            .AddLogging()
            .AddMemoryCache()
            .AddMediatR(cfg =>
            {
                cfg.RegisterServicesFromAssemblyContaining<BulkCreateHabitsCommand>();
                cfg.AddOpenBehavior(typeof(ValidationBehavior<,>));
            })
            .AddSingleton<IValidator<BulkCreateHabitsCommand>, BulkCreateHabitsCommandValidator>()
            .AddSingleton(new BulkCreateHabitsRepositories(
                new GenericRepository<Habit>(context),
                new GenericRepository<GoogleCalendarSyncSuggestion>(context),
                new GenericRepository<Tag>(context)))
            .AddSingleton(_payGate)
            .AddSingleton(dates)
            .AddSingleton<IUnitOfWork>(work)
            .BuildServiceProvider();

        var tool = new BulkCreateHabitsTool(_provider.GetRequiredService<IMediator>());
        var catalog = new AgentCatalogService([tool]);
        var policy = new AgentPolicyEvaluator(context, catalog, _pending, bridge, settings);
        _audit.RecordAsync(Arg.Any<AgentAuditEntry>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _lastAudit = call.Arg<AgentAuditEntry>();
                return Task.CompletedTask;
            });
        var ownership = Substitute.For<IAgentTargetOwnershipService>();
        ownership.GetDenialReasonAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _executor = new AgentOperationExecutor(
            catalog, policy, _audit, ownership, bridge,
            new AiToolRegistry([tool]),
            work, NullLogger<AgentOperationExecutor>.Instance);
    }

    [Fact]
    public async Task ConfirmedDailyItemsWithoutQuantity_CreateEveryItemWithQuantityOne()
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            habits = Enumerable.Range(1, 12).Select(index => new
            {
                title = $"Daily habit {index}",
                frequency_unit = "day"
            })
        });
        var request = await ConfirmAsync(arguments);

        var response = await _executor.ExecuteAsync(request);

        response.Operation.Status.Should().Be(AgentOperationStatus.Succeeded, "audit error: {0}", _lastAudit?.Error);
        using var verify = _factory.CreateContext();
        var habits = verify.Habits.ToList();
        habits.Should().HaveCount(12);
        habits.Should().AllSatisfy(habit =>
        {
            habit.FrequencyUnit.Should().Be(FrequencyUnit.Day);
            habit.FrequencyQuantity.Should().Be(1);
        });
        verify.PendingAgentOperations.Single().ConsumedAtUtc.Should().NotBeNull();
    }

    private async Task<AgentExecuteOperationRequest> ConfirmAsync(JsonElement arguments)
    {
        var request = new AgentExecuteOperationRequest(
            _userId, "bulk_create_habits", arguments, AgentExecutionSurface.Chat, AgentAuthMethod.Jwt,
            CorrelationId: "bulk-create-test");
        var preview = await _executor.ExecuteAsync(request);
        preview.Operation.Status.Should().Be(AgentOperationStatus.PendingConfirmation, "audit error: {0}", _lastAudit?.Error);
        var confirmation = _pending.Confirm(_userId, preview.PendingOperation!.Id);
        confirmation.Should().NotBeNull();
        var execution = _pending.GetExecution(_userId, confirmation!.PendingOperationId);
        execution.Should().NotBeNull();
        return request with { Arguments = execution!.Arguments, ConfirmationToken = confirmation.ConfirmationToken };
    }

    public void Dispose()
    {
        _provider.Dispose();
        _factory.Dispose();
    }
}
