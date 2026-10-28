using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orbit.Api.Controllers;
using Orbit.Application.Behaviors;
using Orbit.Application.Chat;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;
using Orbit.Domain.Entities;
using Orbit.Domain.Interfaces;
using Orbit.Domain.Models;
using Orbit.Infrastructure.Configuration;
using Orbit.Infrastructure.Persistence;
using Orbit.Infrastructure.Services;
using Orbit.Infrastructure.Tests.Persistence;

namespace Orbit.Infrastructure.Tests.Controllers;

public class StagingEvalPendingOperationsTests
{
    [Fact]
    public void FixtureRoutes_ProvideCleanupAndIndependentReadback()
    {
        var controller = typeof(AiController).Assembly.GetType("Orbit.Api.Controllers.StagingEvalController");
        controller.Should().NotBeNull("eval cleanup needs an authoritative fixture independent of conversation recording");
        controller!.GetMethods().Should().Contain(method => method.IsDefined(typeof(HttpDeleteAttribute), false));
        controller.GetMethods().Should().Contain(method => method.IsDefined(typeof(HttpGetAttribute), false));
    }

    [Fact]
    public async Task RepeatAttempts_ClearEveryStoredKindAndReadBackTheRealResponseAcrossFreshScopes()
    {
        using var database = new SqliteOrbitDbContextFactory();
        var services = new ServiceCollection().AddLogging();
        services.AddControllers();
        services.AddScoped(_ => database.CreateContext());
        services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
        services.AddSingleton(new DatabaseConnectionSettings());
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddValidatorsFromAssemblyContaining<ClearEvalPendingOperationsCommandValidator>();
        services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<ClearEvalPendingOperationsCommand>();
            configuration.AddOpenBehavior(typeof(ConcurrencyRetryBehavior<,>));
            configuration.AddOpenBehavior(typeof(ValidationBehavior<,>));
        });
        using var provider = services.BuildServiceProvider();
        var owner = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var catalog = new AgentCatalogService();
        var capability = catalog.GetCapabilityByChatTool("delete_habit")!;
        var approved = Create(owner, "delete_habit", capability);
        database.Context.PendingAgentOperations.Add(approved);
        database.Context.SaveChanges();
        var confirmation = new PendingAgentOperationStore(database.Context, Options.Create(new AgentPlatformSettings()))
            .Confirm(owner, approved.Id)!;
        var foreignOperation = Create(foreign, "delete_habit", capability);
        database.Context.PendingAgentOperations.Add(foreignOperation);
        database.Context.SaveChanges();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var states = catalog.GetOperations().Select(operation => Create(owner, operation.Id, capability)).ToList();
            states.Add(Create(owner, AccountChangeSet.OperationName, capability));
            var expired = Create(owner, "expired_preview", capability, expired: true);
            states.Add(expired);
            var legacy = Create(owner, "legacy_preview", capability);
            states.Add(legacy);
            database.Context.PendingAgentOperations.AddRange(states);
            database.Context.Entry(legacy).Property(item => item.ArgumentSchemaVersion).CurrentValue = 0;
            database.Context.SaveChanges();
            database.Context.ConversationTurns.Should().BeEmpty();
            var expected = states.Count + (attempt == 0 ? 1 : 0);

            (await Send(provider, owner, clear: false)).GetProperty("pendingCount").GetInt32().Should().Be(expected);
            (await Send(provider, owner, clear: true)).GetProperty("cancelledCount").GetInt32().Should().Be(expected);
            (await Send(provider, owner, clear: false)).GetProperty("pendingCount").GetInt32().Should().Be(0);
            (await Send(provider, owner, clear: true)).GetProperty("cancelledCount").GetInt32().Should().Be(0);
            (await Send(provider, foreign, clear: false)).GetProperty("pendingCount").GetInt32().Should().Be(1);
        }

        using var readback = database.CreateContext();
        var stored = await readback.PendingAgentOperations.Where(item => item.UserId == owner).ToListAsync();
        stored.Should().OnlyContain(item => item.ConsumedAtUtc != null && item.ConfirmationTokenHash == null);
        stored.Where(item => item.Id != approved.Id).Should().OnlyContain(item => item.ConfirmedAtUtc == null);
        readback.Habits.Should().BeEmpty();
        readback.Goals.Should().BeEmpty();
        var store = new PendingAgentOperationStore(readback, Options.Create(new AgentPlatformSettings()));
        store.Confirm(owner, approved.Id).Should().BeNull();
        store.GetConfirmedExecution(owner, capability.Id, approved.OperationFingerprint, confirmation.ConfirmationToken).Should().BeNull();
        store.TryConsumeFreshConfirmation(owner, capability.Id, approved.OperationFingerprint, confirmation.ConfirmationToken, false)
            .Should().BeFalse();
    }

    private static async Task<JsonElement> Send(ServiceProvider provider, Guid owner, bool clear)
    {
        using var scope = provider.CreateScope();
        var controller = new StagingEvalController(scope.ServiceProvider.GetRequiredService<ISender>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, owner.ToString())], "JwtBearer"))
                }
            }
        };
        var response = (OkObjectResult)(clear
            ? await controller.ClearPendingOperations(default)
            : await controller.GetPendingOperations(default));
        var options = scope.ServiceProvider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        return JsonSerializer.SerializeToElement(response.Value, options);
    }

    private static PendingAgentOperationState Create(Guid owner, string operationId, AgentCapability capability, bool expired = false)
        => PendingAgentOperationState.Create(new()
        {
            UserId = owner,
            Capability = capability,
            OperationId = operationId,
            ArgumentsJson = "{}",
            Summary = "Preview from an earlier eval",
            OperationFingerprint = Guid.NewGuid().ToString(),
            Surface = AgentExecutionSurface.Chat,
            ExpiresAtUtc = TimeProvider.System.GetUtcNow().UtcDateTime.AddMinutes(expired ? -10 : 10)
        });
}
